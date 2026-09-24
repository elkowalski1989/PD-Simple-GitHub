using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Manufacturing;

namespace PD.PcbTools.Manufacturing;

/// <summary>
/// Engine-backed manufacturing export runner bound at release integration.
/// Drives the centrally pinned Engine exporters
/// (<c>AllegroWorkspaceManufacturing.ExecuteArtworkAsync</c> /
/// <c>ExecuteIpc2581Async</c> over <c>ProcessManufacturingNativeLauncher</c>)
/// against the application's shared Engine session. ODB++ headless execution
/// stays an explicit Engine-reported NoGo: the installed tree carries no
/// supported launcher, so the page plans and validates but never launches.
/// Staging ownership transfers to the caller for explicit approved promotion;
/// a manifest on disk is never treated as success — only a
/// <c>Complete</c> Engine validation result with held staging may promote.
/// </summary>
public sealed class EngineManufacturingExportRunner(AllegroWorkspace workspace)
    : IManufacturingExportRunner
{
    private readonly AllegroWorkspace _workspace =
        workspace ?? throw new ArgumentNullException(nameof(workspace));
    private readonly ProcessManufacturingNativeLauncher _launcher = new();

    public async Task<StagedArtworkResult> RunArtworkAsync(
        ArtworkJobPlan plan,
        ManufacturingRunnerContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);
        string root = ResolveRoot(context);
        ManufacturingNativeToolPresence tool =
            ManufacturingNativeToolset.RequireArtwork(root);
        string artParam = context.ArtParamText
            ?? throw new InvalidOperationException(
                "Artwork export needs caller-supplied art_param.txt text; the Engine never authors it.");
        var options = new ArtworkExecutionOptions(
            context.Timeout,
            new ArtworkParameterFiles(artParam, context.ArtAperText));
        ArtworkNativeExecution execution = await _workspace.Manufacturing
            .ExecuteArtworkAsync(
                plan, context.ApprovedRoot, tool, _launcher, options,
                cancellationToken)
            .ConfigureAwait(false);
        return new(execution.Result, execution.Staging);
    }

    public async Task<StagedIpc2581Result> RunIpc2581Async(
        Ipc2581JobPlan plan,
        ManufacturingRunnerContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);
        string root = ResolveRoot(context);
        ManufacturingNativeToolPresence tool =
            ManufacturingNativeToolset.RequireIpc2581Out(root);
        var options = new Ipc2581ExecutionOptions(
            context.Timeout,
            new Ipc2581ConfigurationFiles(
                context.IpcPropertyText, context.IpcLayerMappingText));
        Ipc2581NativeExecution execution = await _workspace.Manufacturing
            .ExecuteIpc2581Async(
                plan, context.ApprovedRoot, tool, _launcher, options,
                cancellationToken)
            .ConfigureAwait(false);
        return new(execution.Result, execution.Staging);
    }

    public Task<StagedOdbPlusPlusResult> RunOdbPlusPlusAsync(
        OdbPlusPlusJobPlan plan,
        ManufacturingRunnerContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        (OdbPlusPlusOutputResult result, _) = _workspace.Manufacturing
            .ReportOdbPlusPlusHeadless(plan, context.CadenceRoot);
        return Task.FromResult(new StagedOdbPlusPlusResult(result));
    }

    private static string ResolveRoot(ManufacturingRunnerContext context) =>
        context.CadenceRoot
        ?? ManufacturingNativeToolset.FindCadenceRootFromEnvironment()
        ?? throw new InvalidOperationException(
            "No Cadence root: set CDSROOT/CDS_ROOT or choose an approved install directory.");
}
