using PcbEngine.Advanced;
using PcbEngine.PhysicalSymbols;
using PcbEngine.Scenes;

namespace PD.PcbTools;

/// <summary>One explicitly staged DRA journey borrowing the application's selected Engine workspace.</summary>
public sealed class EngineSymbolBindingRunner(PcbWorkspace workspace)
{
    private readonly PcbWorkspace _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private PhysicalSymbolPreparation? _submittedPreparation;
    private CancellationTokenSource? _applyWait;
    private int _busy;
    private bool _needsReconciliation;
    private long _intentRevision;
    private long _preparedRevision;

    public SymbolWorkArea? StagedArea { get; private set; }
    public PhysicalSymbolStageCopy? StageCopy { get; private set; }
    public PhysicalSymbolBinding? Binding { get; private set; }
    public PhysicalSymbolPreparation? Preparation { get; private set; }
    public PhysicalSymbolApplyEvidence? LastApply { get; private set; }
    public PhysicalSymbolPublicationEvidence? LastPublication { get; private set; }
    public PhysicalSymbolReconciliation? Reconciliation { get; private set; }
    public Exception? NotificationFailure { get; private set; }
    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public bool HasUnresolvedOutcome => _needsReconciliation;
    public bool CanStopWaiting => _applyWait is not null;
    public bool CanChangeSession => !IsBusy && !HasUnresolvedOutcome;
    public string? CloseBlockReason => IsBusy ? "Physical-symbol work is still settling." :
        HasUnresolvedOutcome ? "The submitted physical-symbol apply requires its retained operation check; it must not be replayed." : null;
    public static PhysicalSymbolExtensionDescriptor PackagedDescriptor => PhysicalSymbolSkillPackage.Descriptor;
    public IReadOnlyList<PhysicalSymbolCapabilityDimension> Capabilities =>
        Binding?.CapabilityInventory.Operations ?? PhysicalSymbolSkillPackage.CapabilityInventory.Operations;
    public event EventHandler? StateChanged;

    public string StateText
    {
        get
        {
            if (HasUnresolvedOutcome)
            {
                return "Submitted apply outcome is unresolved. Check the original operation; Apply remains consumed.";
            }
            if (IsBusy)
            {
                return "The current stage, inspection or native operation is still settling.";
            }
            if (LastPublication is { } publication)
            {
                return $"Library output: {publication.Status}; artifacts {publication.Artifacts.Count}; " +
                    $"reopen verified={publication.ReopenVerified}. Staged save and library publication are separate evidence.";
            }
            if (Reconciliation is { } reconciliation)
            {
                return $"Original operation check: {reconciliation.Operation?.State.ToString() ?? "unknown"}; " +
                    $"readback {reconciliation.After?.Fingerprint ?? "unavailable"}. {reconciliation.Diagnostic} " +
                    "The original Apply stays consumed; an operation check does not authorize publication.";
            }
            if (LastApply is { } apply)
            {
                return DescribeApply(apply);
            }
            if (Preparation is { } prepared)
            {
                return $"Preview ready for {prepared.Request.Operation} on {prepared.Request.Target.Identity}; " +
                    $"{prepared.AffectedCount} affected, {prepared.Freshness}. Production qualification is checked separately.";
            }
            if (Binding is { } binding)
            {
                return $"Exact packaged extension matched; document {binding.Document.Design}; current={binding.IsCurrent}. " +
                    "Native PACKAGE inspection occurs during Preview. Matching the extension does not grant production qualification.";
            }
            if (StageCopy is { } copy)
            {
                return $"Staged file ready: {copy.Area.StagedDraPath} ({copy.Bytes} bytes; SHA-256 {copy.StagedSha256}). " +
                    "Open this DRA through Allegro's normal file operation, then explicitly select that actual session. " +
                    string.Join(" ", copy.CleanupWarnings);
            }
            return StagedArea is { } area
                ? $"Stage planned at {area.StagedDraPath}. No file was created and no native document was opened."
                : "Select an existing source DRA and a parent directory for a new owned stage.";
        }
    }

    public SymbolWorkArea PlanStage(string stagingRoot, string symbolName, string createdBy)
    {
        if (!_operationGate.Wait(0))
        {
            throw new InvalidOperationException("Another physical-symbol step is still running.");
        }
        try
        {
            RequireResolved();
            SymbolWorkArea area = PhysicalSymbolStagingWorkflow.PlanStage(stagingRoot, symbolName, createdBy);
            ResetJourney();
            StagedArea = area;
            return area;
        }
        finally
        {
            _operationGate.Release();
            Changed();
        }
    }

    public async ValueTask<PhysicalSymbolStageCopy> StageExistingAsync(string sourcePath, string stagingRoot,
        string symbolName, string createdBy, CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireResolved();
            PhysicalSymbolStageCopy staged = await PhysicalSymbolStageCopy.CreateAsync(sourcePath, stagingRoot,
                symbolName, createdBy, cancellationToken).ConfigureAwait(false);
            ResetJourney();
            StageCopy = staged;
            StagedArea = staged.Area;
            return staged;
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>Legacy descriptor entry remains shape-compatible but only the exact embedded package is trusted.</summary>
    public ValueTask<PhysicalSymbolBinding> ActivateAsync(PhysicalSymbolExtensionDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        PhysicalSymbolStagingWorkflow.DiscoverApprovedExtension(descriptor, [PackagedDescriptor]);
        return ActivatePackagedAsync(cancellationToken);
    }

    public async ValueTask<PhysicalSymbolBinding> ActivatePackagedAsync(CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireResolved();
            PhysicalSymbolStageCopy copy = RequireStageCopy();
            await copy.RequireUnchangedAsync(cancellationToken).ConfigureAwait(false);
            RequireSelectedStage();
            string extensionDirectory = Path.Combine(copy.Area.StagingRoot, "engine-module-" + Guid.NewGuid().ToString("N"));
            PhysicalSymbolBinding binding = await _workspace.PhysicalSymbols.ActivatePackagedAsync(
                extensionDirectory, replaceExisting: false, cancellationToken).ConfigureAwait(false);
            RequireSelectedStage();
            Binding = binding;
            LastApply = null;
            LastPublication = null;
            InvalidateIntent();
            return binding;
        }
        finally
        {
            Exit();
        }
    }

    public void InvalidateIntent()
    {
        Interlocked.Increment(ref _intentRevision);
        Preparation = null;
        Changed();
    }

    public PhysicalSymbolRequest BuildRequest(PhysicalSymbolOperation operation,
        PhysicalSymbolTargetIdentity target, PhysicalSymbolPreconditions preconditions,
        PhysicalSymbolPersistencePlan persistence, PhysicalSymbolReadbackExpectation readback,
        PhysicalSymbolIntent intent)
    {
        SymbolWorkArea area = StagedArea ?? throw new InvalidOperationException("Plan and copy a stage first.");
        return PhysicalSymbolStagingWorkflow.ForOperation(area, _workspace.Document, operation,
            target, intent, preconditions, persistence, readback);
    }

    public async ValueTask<PhysicalSymbolPreparation> PrepareAsync(PhysicalSymbolRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireResolved();
            PhysicalSymbolStageCopy copy = RequireStageCopy();
            await copy.RequireUnchangedAsync(cancellationToken).ConfigureAwait(false);
            PhysicalSymbolBinding binding = Binding ?? throw new InvalidOperationException("Match the packaged extension first.");
            PhysicalSymbolStaging.RequireStagedDocument(request, copy.Area);
            if (request.ExpectedDocument != _workspace.Document || !binding.IsCurrent)
            {
                throw new InvalidOperationException("The selected document or extension incarnation changed. Reconnect the staged document and match its package.");
            }
            long revision = Volatile.Read(ref _intentRevision);
            Preparation = null;
            PhysicalSymbolPreparation prepared = await binding.PrepareAsync(request, cancellationToken).ConfigureAwait(false);
            if (revision != Volatile.Read(ref _intentRevision) || request.ExpectedDocument != _workspace.Document || !binding.IsCurrent)
            {
                throw new InvalidOperationException("The intent, stage, document or extension changed while preview was running.");
            }
            await copy.RequireUnchangedAsync(cancellationToken).ConfigureAwait(false);
            _preparedRevision = revision;
            Preparation = prepared;
            return prepared;
        }
        finally
        {
            Exit();
        }
    }

    public bool CanApply => Preparation is { } prepared && !IsBusy && !HasUnresolvedOutcome &&
        prepared.ProductionAssessment.Eligible && _preparedRevision == Volatile.Read(ref _intentRevision);

    public bool CanPublish => LastApply?.Status == PhysicalSymbolApplyStatus.Complete &&
        !IsBusy && !HasUnresolvedOutcome && Binding?.IsCurrent == true &&
        _preparedRevision == Volatile.Read(ref _intentRevision) &&
        _submittedPreparation?.Document == _workspace.Document;

    public async ValueTask<PhysicalSymbolApplyEvidence> ApplyAsync(string approvalIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalIdentity);
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireResolved();
            PhysicalSymbolPreparation prepared = Preparation ?? throw new InvalidOperationException("Preview a frozen request first.");
            PhysicalSymbolProductionAssessment assessment = prepared.ProductionAssessment;
            if (!assessment.Eligible)
            {
                throw new NotSupportedException(string.Join(" ", assessment.Diagnostics.Select(item => item.Message)));
            }
            if (_preparedRevision != Volatile.Read(ref _intentRevision) || prepared.Document != _workspace.Document ||
                Binding?.IsCurrent != true)
            {
                Preparation = null;
                throw new InvalidOperationException("The accepted preview is stale.");
            }
            await RequireStageCopy().RequireUnchangedAsync(cancellationToken).ConfigureAwait(false);
            PhysicalSymbolStaging.RequireStagedDocument(prepared.Request, StagedArea!);
            Preparation = null;
            _submittedPreparation = prepared;
            _applyWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Changed();
            try
            {
                PhysicalSymbolApplyEvidence evidence = await prepared.ApplyProductionAsync(
                    new PhysicalSymbolAuthorization(approvalIdentity, prepared.PreviewId, prepared.Plan.PlanHash),
                    _applyWait.Token).ConfigureAwait(false);
                AdoptApplyEvidence(evidence);
                return evidence;
            }
            catch
            {
                _needsReconciliation = prepared.OperationRecordPath is not null;
                throw;
            }
            finally
            {
                _applyWait.Dispose();
                _applyWait = null;
            }
        }
        finally
        {
            Exit();
        }
    }

    private void AdoptApplyEvidence(PhysicalSymbolApplyEvidence evidence)
    {
        LastApply = evidence;
        _needsReconciliation = evidence.Recovery.State == RecoveryState.Required ||
            evidence.Operation.Recovery.State == RecoveryState.Required ||
            evidence.Status is PhysicalSymbolApplyStatus.Uncertain or
                PhysicalSymbolApplyStatus.ReadbackUnavailable or PhysicalSymbolApplyStatus.ReadbackMismatch ||
            evidence.Operation.State is ResultOperationState.Running or ResultOperationState.Uncertain or
                ResultOperationState.AwaitingUserInput or ResultOperationState.WaitingForApplication;
    }

    /// <summary>Cancels only this wait. It does not assert native abort or permit another Apply.</summary>
    public void StopWaiting()
    {
        try
        {
            _applyWait?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The wait settled between the button click and cancellation.
        }
    }

    public async ValueTask<PhysicalSymbolReconciliation> CheckSubmittedAsync(CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string record = _submittedPreparation?.OperationRecordPath ??
                throw new InvalidOperationException("There is no submitted operation record to check.");
            PhysicalSymbolBinding binding = Binding ?? throw new InvalidOperationException("Retain the original staged binding.");
            PhysicalSymbolReconciliation reconciliation = await binding.ReconcileAsync(record, cancellationToken).ConfigureAwait(false);
            Reconciliation = reconciliation;
            _needsReconciliation = reconciliation.RequiresReconciliation;
            return reconciliation;
        }
        finally
        {
            Exit();
        }
    }

    public async ValueTask<PhysicalSymbolPublicationEvidence> PublishDraAsync(PhysicalSymbolPublicationPlan plan,
        string approvalIdentity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireResolved();
            PhysicalSymbolApplyEvidence apply = LastApply ?? throw new InvalidOperationException("Complete and verify Apply before publication.");
            if (_preparedRevision != Volatile.Read(ref _intentRevision) ||
                _submittedPreparation?.Document != _workspace.Document || Binding?.IsCurrent != true)
            {
                throw new InvalidOperationException("Stage, intent, document or extension changed after Apply. Historical evidence cannot authorize this publication.");
            }
            if (StagedArea is null || !SamePath(plan.StagedDraPath, StagedArea.StagedDraPath))
            {
                throw new InvalidOperationException("Publication must name this journey's exact staged DRA.");
            }
            PhysicalSymbolPublicationEvidence publication = await PhysicalSymbolPublisher.PublishAsync(
                apply, plan, approvalIdentity, cancellationToken).ConfigureAwait(false);
            LastPublication = publication;
            return publication;
        }
        finally
        {
            Exit();
        }
    }

    private async ValueTask EnterAsync(CancellationToken cancellationToken)
    {
        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Another physical-symbol step is still running.");
        }
        Volatile.Write(ref _busy, 1);
        Changed();
    }

    private void Exit()
    {
        Volatile.Write(ref _busy, 0);
        _operationGate.Release();
        Changed();
    }

    private void RequireResolved()
    {
        if (HasUnresolvedOutcome)
        {
            throw new InvalidOperationException("Settle the original submitted operation before changing stage or starting another step.");
        }
    }

    private PhysicalSymbolStageCopy RequireStageCopy() => StageCopy ??
        throw new InvalidOperationException("Stage an explicit source DRA copy; a planned path is not a staged file.");

    private void RequireSelectedStage()
    {
        string? design = _workspace.Document.Design;
        if (design is null || StagedArea is null || !SamePath(design, StagedArea.StagedDraPath))
        {
            throw new InvalidOperationException("Open the copied DRA normally and explicitly select its actual session. The current document is not the staged DRA.");
        }
    }

    private static bool SamePath(string first, string second) => string.Equals(Path.GetFullPath(first), Path.GetFullPath(second),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private void ResetJourney()
    {
        StageCopy = null;
        Binding = null;
        Preparation = null;
        LastApply = null;
        LastPublication = null;
        Reconciliation = null;
        _submittedPreparation = null;
        Interlocked.Increment(ref _intentRevision);
    }

    private void Changed()
    {
        if (StateChanged is not { } observers)
        {
            return;
        }
        foreach (EventHandler observer in observers.GetInvocationList())
        {
            try
            {
                observer(this, EventArgs.Empty);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                NotificationFailure = error;
            }
        }
    }

    public static string DescribeApply(PhysicalSymbolApplyEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return $"Apply {evidence.Status}; native database {evidence.Execution.DatabasePhase}; " +
            $"staged DRA save {evidence.Execution.DraSave}; verification {evidence.Execution.Verification}. " +
            $"Before {evidence.Before.Fingerprint}; after {evidence.After?.Fingerprint ?? "unavailable"}. " +
            $"{string.Join("; ", evidence.ReadbackDiagnostics)} {evidence.PhysicalRecovery.Limitation}";
    }
}
