using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Manufacturing;

namespace PD.PcbTools.Manufacturing;

/// <summary>
/// Caller-chosen roots and configuration for one manufacturing run. The
/// Engine-backed runner (bound at integration) consumes these; the default
/// unqualified runner ignores them and reports NoGo.
/// </summary>
public sealed record ManufacturingRunnerContext(
    string ApprovedRoot,
    string? CadenceRoot,
    TimeSpan Timeout,
    string? ArtParamText = null,
    string? ArtAperText = null,
    string? IpcPropertyText = null,
    string? IpcLayerMappingText = null);

/// <summary>Engine result plus the caller-owned staging area, if any.</summary>
public sealed record StagedArtworkResult(ArtworkOutputResult Result, ManufacturingStagingArea? Staging);

/// <summary>Engine result plus the caller-owned staging area, if any.</summary>
public sealed record StagedOdbPlusPlusResult(OdbPlusPlusOutputResult Result);

/// <summary>Engine result plus the caller-owned staging area, if any.</summary>
public sealed record StagedIpc2581Result(Ipc2581OutputResult Result, ManufacturingStagingArea? Staging);

/// <summary>
/// PD-owned export seam. Implementations drive typed Engine plans to results;
/// staging ownership transfers to the caller for explicit approved promotion.
/// </summary>
public interface IManufacturingExportRunner
{
    Task<StagedArtworkResult> RunArtworkAsync(
        ArtworkJobPlan plan,
        ManufacturingRunnerContext context,
        CancellationToken cancellationToken = default);

    Task<StagedOdbPlusPlusResult> RunOdbPlusPlusAsync(
        OdbPlusPlusJobPlan plan,
        ManufacturingRunnerContext context,
        CancellationToken cancellationToken = default);

    Task<StagedIpc2581Result> RunIpc2581Async(
        Ipc2581JobPlan plan,
        ManufacturingRunnerContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Default runner before the qualified native binding lands: every format
/// reports the Engine's explicit NoGo admission with its feasibility reason.
/// Nothing is launched, no staging is created, and promotion stays disabled.
/// </summary>
public sealed class UnqualifiedManufacturingRunner : IManufacturingExportRunner
{
    public Task<StagedArtworkResult> RunArtworkAsync(
        ArtworkJobPlan plan,
        ManufacturingRunnerContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new StagedArtworkResult(ManufacturingOutputAdmission.RejectArtwork(plan), null));
    }

    public Task<StagedOdbPlusPlusResult> RunOdbPlusPlusAsync(
        OdbPlusPlusJobPlan plan,
        ManufacturingRunnerContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new StagedOdbPlusPlusResult(ManufacturingOutputAdmission.RejectOdbPlusPlus(plan)));
    }

    public Task<StagedIpc2581Result> RunIpc2581Async(
        Ipc2581JobPlan plan,
        ManufacturingRunnerContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new StagedIpc2581Result(ManufacturingOutputAdmission.RejectIpc2581(plan), null));
    }
}
