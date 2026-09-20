using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Manufacturing;

namespace PD.PcbTools.Manufacturing;

public enum ManufacturingPageSourceState
{
    Disconnected,
    NoCanonicalSource,
    Ready
}

public sealed record ManufacturingSourceStatus(
    ManufacturingPageSourceState State,
    string Detail,
    ManufacturingSourceCapture? Capture);

public sealed record ManufacturingFilmInput(
    string Film,
    string Layers,
    string Artifact,
    bool Negative,
    bool Mirror);

/// <summary>
/// Offline-capable Manufacturing page model. The page opens disconnected with
/// setup guidance; every action reports its own availability and reason.
/// Plan construction is pure and returns displayable errors instead of
/// throwing; runs delegate to the injected export runner.
/// </summary>
public sealed class ManufacturingPageModel
{
    private readonly HashSet<Guid> _promotedJobs = new();

    public ManufacturingSourceStatus SourceStatus { get; private set; } =
        new(ManufacturingPageSourceState.Disconnected, "Connect to Allegro with a board open to plan manufacturing output.", null);

    public bool IsConnected { get; private set; }

    public StagedArtworkResult? LastArtwork { get; private set; }

    public StagedOdbPlusPlusResult? LastOdbPlusPlus { get; private set; }

    public StagedIpc2581Result? LastIpc2581 { get; private set; }

    public void SetConnected(bool connected)
    {
        IsConnected = connected;
        if (!connected)
        {
            SourceStatus = new(
                ManufacturingPageSourceState.Disconnected,
                "Connect to Allegro with a board open to plan manufacturing output.",
                null);
        }
    }

    /// <summary>
    /// Captures the planning source fence. The acquirer is the live workspace
    /// capture; its refusal (no canonical saved source) becomes an explicit
    /// status, never an exception in the page.
    /// </summary>
    public void RefreshSource(Func<ManufacturingSourceCapture> acquire)
    {
        ArgumentNullException.ThrowIfNull(acquire);
        if (!IsConnected)
        {
            SourceStatus = new(
                ManufacturingPageSourceState.Disconnected,
                "Connect to Allegro with a board open to plan manufacturing output.",
                null);
            return;
        }
        try
        {
            ManufacturingSourceCapture capture = acquire();
            SourceStatus = new(
                ManufacturingPageSourceState.Ready,
                $"Source fenced at snapshot revision {capture.SnapshotRevision}.",
                capture);
        }
        catch (InvalidOperationException exception)
        {
            SourceStatus = new(
                ManufacturingPageSourceState.NoCanonicalSource,
                exception.Message,
                null);
        }
    }

    public bool CanPlan(out string reason)
    {
        if (SourceStatus is { State: ManufacturingPageSourceState.Ready, Capture: not null })
        {
            reason = string.Empty;
            return true;
        }
        reason = SourceStatus.Detail;
        return false;
    }

    public (ArtworkJobPlan? Plan, string? Error) TryBuildArtwork(
        IReadOnlyList<ManufacturingFilmInput> films,
        ArtworkOptions options,
        string destinationDirectory,
        bool replaceIfExists)
    {
        if (!CanPlan(out string planReason))
        {
            return (null, planReason);
        }
        try
        {
            ArgumentNullException.ThrowIfNull(films);
            ArgumentNullException.ThrowIfNull(options);
            var filmPlans = new List<ArtworkFilmPlan>();
            foreach (ManufacturingFilmInput film in films)
            {
                string[] layers = SplitNames(film.Layers);
                if (layers.Length == 0)
                {
                    return (null, $"Film '{film.Film}' needs at least one layer.");
                }
                filmPlans.Add(new(
                    film.Film,
                    layers,
                    new(film.Artifact),
                    film.Negative ? ArtworkPolarity.Negative : ArtworkPolarity.Positive,
                    film.Mirror));
            }
            var destination = new ManufacturingDestination(
                new(destinationDirectory),
                replaceIfExists ? ManufacturingOverwritePolicy.ReplaceAfterValidation : ManufacturingOverwritePolicy.RejectExisting);
            return (new(Guid.NewGuid(), SourceStatus.Capture!, filmPlans, options, destination), null);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return (null, exception.Message);
        }
    }

    public (OdbPlusPlusJobPlan? Plan, string? Error) TryBuildOdbPlusPlus(
        string stepName,
        string layers,
        OdbPlusPlusOptions options,
        string destinationDirectory,
        bool replaceIfExists)
    {
        if (!CanPlan(out string planReason))
        {
            return (null, planReason);
        }
        try
        {
            string[] layerNames = SplitNames(layers);
            if (layerNames.Length == 0)
            {
                return (null, "ODB++ output needs at least one layer.");
            }
            ArgumentNullException.ThrowIfNull(options);
            var destination = new ManufacturingDestination(
                new(destinationDirectory),
                replaceIfExists ? ManufacturingOverwritePolicy.ReplaceAfterValidation : ManufacturingOverwritePolicy.RejectExisting);
            return (new(Guid.NewGuid(), SourceStatus.Capture!, stepName, layerNames, options, destination), null);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return (null, exception.Message);
        }
    }

    public (Ipc2581JobPlan? Plan, string? Error) TryBuildIpc2581(
        string layers,
        string artifact,
        Ipc2581Options options,
        string destinationDirectory,
        bool replaceIfExists)
    {
        if (!CanPlan(out string planReason))
        {
            return (null, planReason);
        }
        try
        {
            string[] layerNames = SplitNames(layers);
            if (layerNames.Length == 0)
            {
                return (null, "IPC-2581 output needs at least one layer.");
            }
            ArgumentNullException.ThrowIfNull(options);
            var destination = new ManufacturingDestination(
                new(destinationDirectory),
                replaceIfExists ? ManufacturingOverwritePolicy.ReplaceAfterValidation : ManufacturingOverwritePolicy.RejectExisting);
            return (new(Guid.NewGuid(), SourceStatus.Capture!, layerNames, new(artifact), options, destination), null);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return (null, exception.Message);
        }
    }

    /// <summary>Expected output inventory for the plan preview, before any run.</summary>
    public static IReadOnlyList<string> PreviewArtworkManifest(ArtworkJobPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.ExpectedArtifacts.Select(item => item.Path.Value).ToList();
    }

    public static IReadOnlyList<string> PreviewOdbManifest(OdbPlusPlusJobPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.ExpectedArtifacts.Select(item => $"{item.Path.Value} ({item.Role})").ToList();
    }

    public static IReadOnlyList<string> PreviewIpcManifest(Ipc2581JobPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.ExpectedArtifacts.Select(item => item.Path.Value).ToList();
    }

    public async Task<StagedArtworkResult> RunArtworkAsync(
        ArtworkJobPlan plan,
        ManufacturingRunnerContext context,
        IManufacturingExportRunner runner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(runner);
        LastArtwork?.Staging?.Dispose();
        LastArtwork = await runner.RunArtworkAsync(plan, context, cancellationToken).ConfigureAwait(false);
        return LastArtwork;
    }

    public async Task<StagedOdbPlusPlusResult> RunOdbPlusPlusAsync(
        OdbPlusPlusJobPlan plan,
        ManufacturingRunnerContext context,
        IManufacturingExportRunner runner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(runner);
        LastOdbPlusPlus = await runner.RunOdbPlusPlusAsync(plan, context, cancellationToken).ConfigureAwait(false);
        return LastOdbPlusPlus;
    }

    public async Task<StagedIpc2581Result> RunIpc2581Async(
        Ipc2581JobPlan plan,
        ManufacturingRunnerContext context,
        IManufacturingExportRunner runner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(runner);
        LastIpc2581?.Staging?.Dispose();
        LastIpc2581 = await runner.RunIpc2581Async(plan, context, cancellationToken).ConfigureAwait(false);
        return LastIpc2581;
    }

    public bool CanPromote(Guid jobId, ManufacturingOutputState state, bool hasStaging, out string reason)
    {
        if (state != ManufacturingOutputState.Complete)
        {
            reason = "Promotion needs a Complete validation result; nothing else may leave staging.";
            return false;
        }
        if (!hasStaging)
        {
            reason = "No staged output is held for this job.";
            return false;
        }
        if (_promotedJobs.Contains(jobId))
        {
            reason = "This job was already promoted or released.";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Commits staged output to its destination after explicit approval.
    /// Returns the destination path; the model releases the staging handle.
    /// </summary>
    public string Promote(Guid jobId, ManufacturingOutputState state, ManufacturingStagingArea? staging)
    {
        ArgumentNullException.ThrowIfNull(staging);
        if (!CanPromote(jobId, state, true, out string reason))
        {
            throw new InvalidOperationException(reason);
        }
        string destination = staging.Commit();
        _promotedJobs.Add(jobId);
        staging.Dispose();
        if (LastArtwork?.Result.JobId == jobId)
        {
            LastArtwork = LastArtwork with { Staging = null };
        }
        if (LastIpc2581?.Result.JobId == jobId)
        {
            LastIpc2581 = LastIpc2581 with { Staging = null };
        }
        return destination;
    }

    public void ReleaseStaging()
    {
        LastArtwork?.Staging?.Dispose();
        LastIpc2581?.Staging?.Dispose();
        if (LastArtwork is not null)
        {
            LastArtwork = LastArtwork with { Staging = null };
        }
        if (LastIpc2581 is not null)
        {
            LastIpc2581 = LastIpc2581 with { Staging = null };
        }
    }

    public static string SummarizeArtwork(ArtworkOutputResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return $"[{result.State}] {result.Code}: {result.Message} " +
            $"Artifacts={result.Artifacts.Length} Missing={result.MissingArtifacts.Length} " +
            $"Invalid={result.InvalidArtifacts.Length} Config={result.ConfigurationRestoration}";
    }

    public static string SummarizeOdb(OdbPlusPlusOutputResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return $"[{result.State}] {result.Code}: {result.Message} " +
            $"Artifacts={result.Artifacts.Length} Missing={result.MissingArtifacts.Length} " +
            $"Config={result.ConfigurationRestoration}";
    }

    public static string SummarizeIpc(Ipc2581OutputResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return $"[{result.State}] {result.Code}: {result.Message} " +
            $"Artifact={(result.Artifact is null ? "none" : result.Artifact.Path.Value)} " +
            $"Config={result.ConfigurationRestoration}";
    }

    private static string[] SplitNames(string value) =>
        (value ?? string.Empty)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => item.Length != 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}
