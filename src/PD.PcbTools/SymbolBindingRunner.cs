using CircuitHub.AllegroBridge.Engine.Live;

namespace PD.PcbTools;

/// <summary>
/// Engine-backed physical-symbol binding/activation workflow bound at release
/// integration. Staging plans a disposable staged PACKAGE work area against
/// the staged 1.13.0-preview.94 Engine contract
/// (<c>EngineSymbolWorkArea.Plan</c>); activation binds the exact trusted
/// extension descriptor (<c>AllegroWorkspacePhysicalSymbols.ActivateAsync</c>);
/// preparation previews without mutating and apply executes once with
/// independent before/after readback; DRA publication validates bytes against
/// the staged source while PSM compilation stays refused inside the Engine
/// publisher. This runner owns no session: the caller supplies the
/// application's shared Engine workspace, and the exact extension descriptor
/// (version + content hash) comes from the extension toolkit's release
/// manifest, never invented here.
/// </summary>
public sealed class EngineSymbolBindingRunner(AllegroWorkspace workspace)
{
    private readonly AllegroWorkspace _workspace =
        workspace ?? throw new ArgumentNullException(nameof(workspace));

    public EngineSymbolWorkArea? StagedArea { get; private set; }

    public EnginePhysicalSymbolBinding? Binding { get; private set; }

    public EnginePhysicalSymbolPreparation? Preparation { get; private set; }

    public EnginePhysicalSymbolApplyEvidence? LastApply { get; private set; }

    public string StateText => (StagedArea, Binding, Preparation, LastApply) switch
    {
        (null, null, _, _) =>
            "No staged PACKAGE work area and no symbol binding. Stage a disposable work area first.",
        (not null, null, _, _) =>
            $"Staged '{StagedArea.SymbolName}' at {StagedArea.StagedDraPath} (identity {StagedArea.StagingIdentity}). " +
            "No symbol binding is active; activation needs the exact release-manifest descriptor.",
        (not null, not null, null, _) =>
            $"Bound to '{Binding.Document.Design ?? "unknown document"}' (current: {Binding.IsCurrent}). " +
            "No preparation is held; preview is non-mutating.",
        (not null, not null, not null, _) =>
            $"Prepared {Preparation.Request.Operation} on '{Preparation.Request.Target.Identity}' " +
            $"(affected {Preparation.AffectedCount}, freshness {Preparation.Freshness}). " +
            "Apply executes once with before/after readback; it is never replayed.",
        (null, not null, _, _) =>
            "A symbol binding is active without a staged work area. Stage the PACKAGE document the binding serves.",
    };

    /// <summary>
    /// Plans a disposable staged PACKAGE work area without touching native
    /// state or the filesystem. A board instance with a similar symbol name
    /// is never that document.
    /// </summary>
    public EngineSymbolWorkArea PlanStage(string stagingRoot, string symbolName, string createdBy)
    {
        EngineSymbolWorkArea area = EngineSymbolWorkArea.Plan(stagingRoot, symbolName, createdBy);
        StagedArea = area;
        Preparation = null;
        return area;
    }

    /// <summary>
    /// Activates the separately packaged physical-symbol extension for the
    /// exact trusted descriptor. Shape errors throw before any native work.
    /// </summary>
    public async ValueTask<EnginePhysicalSymbolBinding> ActivateAsync(
        EnginePhysicalSymbolExtensionDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        descriptor.RequireAccepted();
        EnginePhysicalSymbolBinding binding = await _workspace.PhysicalSymbols
            .ActivateAsync(descriptor, cancellationToken).ConfigureAwait(false);
        Binding = binding;
        Preparation = null;
        LastApply = null;
        return binding;
    }

    /// <summary>
    /// Builds one frozen PACKAGE-symbol request against the workspace's live
    /// document identity — never a fabricated identity. The request is
    /// validated by the Engine validator and, when a staged work area is
    /// held, cross-checked against it before any preview or apply
    /// observation is acquired.
    /// </summary>
    public EnginePhysicalSymbolRequest BuildRequest(
        EnginePhysicalSymbolOperation operation,
        EnginePhysicalSymbolTargetIdentity target,
        EnginePhysicalSymbolPreconditions preconditions,
        EnginePhysicalSymbolPersistencePlan persistence,
        EnginePhysicalSymbolReadbackExpectation readback,
        EnginePhysicalSymbolIntent intent)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(preconditions);
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(readback);
        ArgumentNullException.ThrowIfNull(intent);
        var request = new EnginePhysicalSymbolRequest(
            operation,
            EnginePhysicalSymbolDocumentKind.PackageSymbolDocument,
            _workspace.Document,
            target,
            preconditions,
            persistence,
            readback,
            intent);
        EnginePhysicalSymbolWorkflowValidator.ValidateRequest(request);
        if (StagedArea is not null)
        {
            EnginePhysicalSymbolStaging.RequireStagedDocument(request, StagedArea);
        }
        return request;
    }

    /// <summary>
    /// Previews one frozen request without mutating. When a staged work area
    /// is held, the request must name that staged PACKAGE document; a request
    /// naming another live document is rejected, never rebound.
    /// </summary>
    public async ValueTask<EnginePhysicalSymbolPreparation> PrepareAsync(
        EnginePhysicalSymbolRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Binding is null)
        {
            throw new InvalidOperationException(
                "No symbol binding is active. Activate the exact extension descriptor first.");
        }
        if (StagedArea is not null)
        {
            EnginePhysicalSymbolStaging.RequireStagedDocument(request, StagedArea);
        }
        EnginePhysicalSymbolPreparation preparation = await Binding
            .PrepareAsync(request, cancellationToken).ConfigureAwait(false);
        Preparation = preparation;
        return preparation;
    }

    /// <summary>
    /// Applies the one held preparation exactly once with independent
    /// before/after readback. The preparation is consumed and never replayed.
    /// </summary>
    public async ValueTask<EnginePhysicalSymbolApplyEvidence> ApplyAsync(
        string approvalIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalIdentity);
        if (Preparation is null)
        {
            throw new InvalidOperationException(
                "No preparation is held. Preview a frozen request first; preview is non-mutating.");
        }
        EnginePhysicalSymbolApplyEvidence evidence = await Preparation
            .ApplyAsync(approvalIdentity, cancellationToken).ConfigureAwait(false);
        Preparation = null;
        LastApply = evidence;
        return evidence;
    }

    /// <summary>
    /// Publishes the staged DRA from a completed apply with independent after
    /// readback. Byte validation, destination policy, and the bounded approval
    /// identity are enforced by the Engine publisher; PSM output stays refused.
    /// </summary>
    public async ValueTask<EnginePhysicalSymbolPublicationEvidence> PublishDraAsync(
        EnginePhysicalSymbolPublicationPlan plan,
        string approvalIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalIdentity);
        if (LastApply is null)
        {
            throw new InvalidOperationException(
                "Nothing is published without a completed apply with independent after readback.");
        }
        plan.RequireValid();
        return await EnginePhysicalSymbolPublisher.PublishAsync(
            LastApply, plan, approvalIdentity, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Summarizes one apply evidence with its readback: status, pin/geometry
    /// deltas, before/after fingerprints, and recovery limits. A preview or
    /// staged DRA alone never counts as this evidence.
    /// </summary>
    public static string DescribeApply(EnginePhysicalSymbolApplyEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        string readback = evidence is { Before: not null, After: not null }
            ? $"before fingerprint {evidence.Before.Fingerprint} ({evidence.Before.Pins.Count} pins, " +
              $"{evidence.Before.Geometry.Count} geometry) after fingerprint {evidence.After.Fingerprint} " +
              $"({evidence.After.Pins.Count} pins, {evidence.After.Geometry.Count} geometry), " +
              $"changed {(evidence.Delta?.HasChanges == true ? "yes" : "no")}, " +
              $"unsaved changes {evidence.After.HasUnsavedChanges}"
            : "after readback unavailable";
        string diagnostics = evidence.ReadbackDiagnostics.Count == 0
            ? "no readback diagnostics"
            : string.Join("; ", evidence.ReadbackDiagnostics);
        return $"Apply {evidence.Status} (freshness {evidence.Freshness}). {readback}. {diagnostics}. " +
            $"Board database recovery {evidence.PhysicalRecovery.BoardDatabase}; " +
            $"library files {evidence.PhysicalRecovery.LibraryFiles}: {evidence.PhysicalRecovery.Limitation}";
    }
}
