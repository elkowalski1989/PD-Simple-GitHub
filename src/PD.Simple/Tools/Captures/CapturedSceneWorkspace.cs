using System.Collections.Immutable;
using System.IO;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.Simple.Tools.Analysis;

namespace PD.Simple.Tools.Captures;

/// <summary>
/// T08 Captured scenes workflow over public Engine APIs. Live acquisition
/// (<see cref="AllegroWorkspace.ReadAsync"/>, bulk capture) runs only through
/// host-supplied delegates so this workspace never owns a session; archives
/// (<see cref="SceneArchive"/>) and bulk replay open offline with no native
/// authority. Every Run after same-path reopen acquires anew: archived scenes
/// are flagged offline-only and can never authorize live work.
/// </summary>
public sealed class CapturedSceneWorkspace : LaneAWorkspaceBase, IAsyncDisposable
{
    public const int DefaultMaximumObjects = 50_000;
    public const int MaximumMaximumObjects = 1_000_000;

    private readonly Func<SceneQuery, IProgress<EngineDiagnostic>?, CancellationToken, Task<DesignScene>>? _acquireScene;
    private readonly Func<EngineBulkCaptureOptions, IProgress<EngineDiagnostic>?, CancellationToken, Task<EngineBulkCapture>>? _acquireBulk;

    private DesignScene? _scene;
    private string _sourceKind = "none";
    private bool _liveAuthority;
    private string? _sourcePath;
    private EngineBulkCapture? _bulk;
    private string? _bulkPath;
    private bool _disposed;

    private ImmutableArray<DataFamily> _families =
        ImmutableArray.Create(DataFamily.Copper, DataFamily.Nets, DataFamily.Components, DataFamily.Layers);
    private bool _includeContours;
    private ImmutableArray<string> _layers = ImmutableArray<string>.Empty;
    private bool _hasRegion;
    private decimal _regionX1Mils;
    private decimal _regionY1Mils;
    private decimal _regionX2Mils = 1000m;
    private decimal _regionY2Mils = 1000m;
    private int _maximumObjects = DefaultMaximumObjects;
    private int _maximumContourVertices = 250_000;
    private bool _useBulkPath;
    private readonly List<EngineDiagnostic> _progressDiagnostics = [];
    private string _coverageSummary = "No input attached.";
    private bool _lastReplayIsNewIdentity;

    /// <param name="acquireScene">
    /// Host live acquisition (normally <c>AllegroWorkspace.ReadAsync</c>
    /// projected to its scene). Null keeps live capture unavailable.
    /// </param>
    /// <param name="acquireBulk">
    /// Host bulk acquisition (normally
    /// <c>AllegroWorkspace.CaptureBulkSceneAsync</c>). Null keeps bulk
    /// capture unavailable.
    /// </param>
    public CapturedSceneWorkspace(
        Func<SceneQuery, IProgress<EngineDiagnostic>?, CancellationToken, Task<DesignScene>>? acquireScene = null,
        Func<EngineBulkCaptureOptions, IProgress<EngineDiagnostic>?, CancellationToken, Task<EngineBulkCapture>>? acquireBulk = null)
    {
        _acquireScene = acquireScene;
        _acquireBulk = acquireBulk;
    }

    public bool HasScene => _scene is not null;

    public string SourceKind => _sourceKind;

    public Guid? SourceCaptureId => _scene?.Identity.CaptureId;

    /// <summary>
    /// True only when the attached scene arrived through this session's live
    /// acquisition. Archive, example, and replay inputs are offline-only.
    /// </summary>
    public bool LiveAuthority => _liveAuthority && _scene is not null;

    public string? SourcePath => _sourcePath;

    public string SourceProvenance =>
        _scene is null
            ? "none"
            : $"{_scene.Identity.Provenance.Provider} {_scene.Identity.Provenance.Version} " +
              $"({_scene.Identity.Provenance.OriginalAcquisition}, offline={_scene.Identity.Provenance.IsOffline})";

    public string CoverageSummary
    {
        get => _coverageSummary;
        private set => SetField(ref _coverageSummary, value);
    }

    public ImmutableArray<DataFamily> Families
    {
        get => _families;
        set
        {
            if (SetField(ref _families, value.IsDefault ? ImmutableArray<DataFamily>.Empty : value))
            {
                Raise(nameof(CanCapture));
                Raise(nameof(CaptureBlockedReason));
            }
        }
    }

    public bool IncludeContours
    {
        get => _includeContours;
        set
        {
            if (SetField(ref _includeContours, value))
            {
                Raise(nameof(CanCapture));
                Raise(nameof(CaptureBlockedReason));
            }
        }
    }

    public string LayerNamesText
    {
        get => string.Join(",", _layers);
        set
        {
            ImmutableArray<string> parsed = value is null
                ? ImmutableArray<string>.Empty
                : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToImmutableArray();
            if (SetField(ref _layers, parsed))
            {
                Raise(nameof(CanCapture));
                Raise(nameof(CaptureBlockedReason));
            }
        }
    }

    public bool HasRegion => _hasRegion;

    public decimal RegionX1Mils
    {
        get => _regionX1Mils;
        set { if (SetField(ref _regionX1Mils, value)) { MarkRegion(); } }
    }

    public decimal RegionY1Mils
    {
        get => _regionY1Mils;
        set { if (SetField(ref _regionY1Mils, value)) { MarkRegion(); } }
    }

    public decimal RegionX2Mils
    {
        get => _regionX2Mils;
        set { if (SetField(ref _regionX2Mils, value)) { MarkRegion(); } }
    }

    public decimal RegionY2Mils
    {
        get => _regionY2Mils;
        set { if (SetField(ref _regionY2Mils, value)) { MarkRegion(); } }
    }

    public int MaximumObjects
    {
        get => _maximumObjects;
        set => SetField(ref _maximumObjects, Math.Clamp(value, 1, MaximumMaximumObjects));
    }

    public int MaximumContourVertices
    {
        get => _maximumContourVertices;
        set => SetField(ref _maximumContourVertices, Math.Max(1, value));
    }

    public bool UseBulkPath
    {
        get => _useBulkPath;
        set
        {
            if (SetField(ref _useBulkPath, value))
            {
                Raise(nameof(CanCapture));
                Raise(nameof(CaptureBlockedReason));
            }
        }
    }

    public IReadOnlyList<EngineDiagnostic> ProgressDiagnostics => _progressDiagnostics;

    public EngineBulkCaptureInfo? BulkInfo => _bulk?.Info;

    public string BulkInfoText =>
        _bulk is null
            ? "No bulk capture is held."
            : $"Bulk {_bulk.Info.Identity.CaptureToken}: {_bulk.Info.PageCount} page(s), " +
              $"{_bulk.Info.RecordCount} record(s), {_bulk.Info.StoredBytes} byte(s), " +
              $"complete coverage={_bulk.Info.HasCompleteCoverage}.";

    public string LiveAuthorityText =>
        _scene is null
            ? "No input attached."
            : _liveAuthority
                ? "Live authority: current session capture; may back live work."
                : "Live authority: none. This offline-only input cannot authorize live work.";

    public bool LastReplayIsNewIdentity => _lastReplayIsNewIdentity;

    public bool CanCapture => DescribeCaptureReadiness().IsReady;

    public string CaptureBlockedReason =>
        DescribeCaptureReadiness() is { IsReady: true }
            ? "Ready to capture."
            : DescribeCaptureReadiness().Reason + " Next: " + DescribeCaptureReadiness().NextStep;

    private void MarkRegion()
    {
        _hasRegion = true;
        Raise(nameof(HasRegion));
        Raise(nameof(CanCapture));
        Raise(nameof(CaptureBlockedReason));
    }

    public void ClearRegion()
    {
        _hasRegion = false;
        Raise(nameof(HasRegion));
        Raise(nameof(CanCapture));
        Raise(nameof(CaptureBlockedReason));
    }

    /// <summary>
    /// Explicit capture scope: families, contour opt-in, layers, region, and
    /// budgets. Contours stay off unless the caller opts in.
    /// </summary>
    public SceneQuery BuildSceneQuery()
    {
        ImmutableArray<LayerId> layers = _layers.IsDefaultOrEmpty
            ? ImmutableArray<LayerId>.Empty
            : _layers.Select(name => new LayerId(name)).ToImmutableArray();
        DesignBounds? region = null;
        if (_hasRegion)
        {
            decimal x1 = Math.Min(_regionX1Mils, _regionX2Mils);
            decimal x2 = Math.Max(_regionX1Mils, _regionX2Mils);
            decimal y1 = Math.Min(_regionY1Mils, _regionY2Mils);
            decimal y2 = Math.Max(_regionY1Mils, _regionY2Mils);
            region = new DesignBounds(
                DesignPoint.From(x1, y1, LengthUnit.Mils),
                DesignPoint.From(x2, y2, LengthUnit.Mils));
        }
        return new SceneQuery
        {
            Families = _families.IsDefaultOrEmpty
                ? ImmutableArray.Create(DataFamily.Copper)
                : _families,
            IncludeContours = _includeContours,
            MaximumObjects = _maximumObjects,
            Layers = layers,
            Region = region,
        };
    }

    public EngineBulkCaptureOptions BuildBulkOptions() =>
        new()
        {
            IncludeContours = _includeContours,
            MaximumObjectRecords = _maximumObjects,
            MaximumContourVertices = _maximumContourVertices,
        };

    public ToolReadiness DescribeCaptureReadiness()
    {
        if (_useBulkPath && _acquireBulk is null)
        {
            return ToolReadiness.Blocked(
                "Bulk capture is unavailable in this context.",
                "Turn off the bulk path or connect a live session first.");
        }
        if (!_useBulkPath && _acquireScene is null)
        {
            return ToolReadiness.Blocked(
                "Live capture is unavailable in this context.",
                "Open an archive or example to work offline, or connect a live session.");
        }
        if (_families.IsDefaultOrEmpty)
        {
            return ToolReadiness.Blocked(
                "No families are selected.",
                "Select at least the Copper family.");
        }
        if (_hasRegion && (_regionX1Mils == _regionX2Mils || _regionY1Mils == _regionY2Mils))
        {
            return ToolReadiness.Blocked(
                "The region has zero area.",
                "Correct the rectangle coordinates or clear the region.");
        }
        return ToolReadiness.Ready();
    }

    /// <summary>
    /// Captures now through the host delegate with progress and coverage.
    /// Each call issues a fresh native request; reattaching the same archive
    /// path never counts as a new capture.
    /// </summary>
    public async Task<LaneAOperationResult> CaptureNowAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        Guid correlation = Guid.NewGuid();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ToolReadiness readiness = DescribeCaptureReadiness();
        if (!readiness.IsReady)
        {
            return FailResult(correlation, "CaptureNow", readiness.Reason, started);
        }
        IsBusy = true;
        _progressDiagnostics.Clear();
        var progress = new Progress<EngineDiagnostic>(diagnostic => _progressDiagnostics.Add(diagnostic));
        StatusMessage = "Capturing the current native document…";
        try
        {
            if (_useBulkPath)
            {
                EngineBulkCapture bulk = await _acquireBulk!(
                    BuildBulkOptions(), progress, cancellationToken);
                await ReplaceBulkAsync(bulk, null);
                StatusMessage =
                    $"Bulk capture sealed: {bulk.Info.PageCount} page(s), {bulk.Info.RecordCount} record(s), " +
                    $"{bulk.Info.StoredBytes} byte(s), complete coverage={bulk.Info.HasCompleteCoverage}. " +
                    "Replay a query to materialize a scene.";
                return SuccessResult(
                    correlation, "CaptureNow", "CaptureBulkNow", StatusMessage, started);
            }
            DesignScene fresh = await _acquireScene!(
                BuildSceneQuery(), progress, cancellationToken);
            AttachSceneInternal(fresh, "live", liveAuthority: true, path: null);
            string outcome = $"Captured fresh scene {fresh.Identity.CaptureId} " +
                $"({_progressDiagnostics.Count} progress diagnostic(s)).";
            StatusMessage = outcome;
            return SuccessResult(correlation, "CaptureNow", "CaptureNow", outcome, started);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "Capture canceled; owned readers and files were released.";
            return FailResult(correlation, "CaptureNow", "Canceled; nothing was attached.", started);
        }
        catch (Exception error)
        {
            StatusMessage = "Capture failed: " + error.Message;
            return FailResult(correlation, "CaptureNow", "Failed: " + error.Message, started);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Replays a selective query against the held bulk capture on a
    /// background thread. Only selected pages cross into the scene; the bulk
    /// store stays sealed.
    /// </summary>
    public async Task<LaneAOperationResult> ReplayBulkAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        Guid correlation = Guid.NewGuid();
        ObjectDisposedException.ThrowIf(_disposed, this);
        EngineBulkCapture? bulk = _bulk;
        if (bulk is null)
        {
            return FailResult(correlation, "ReplayBulk", "No bulk capture is held.", started);
        }
        Guid? previous = _scene?.Identity.CaptureId;
        IsBusy = true;
        StatusMessage = "Replaying the selected scope from the sealed bulk capture…";
        try
        {
            EngineBulkReplayResult replayed = await bulk.ReplayAsync(BuildSceneQuery(), cancellationToken);
            _lastReplayIsNewIdentity = !previous.HasValue || previous.Value != replayed.Scene.Identity.CaptureId;
            AttachSceneInternal(replayed.Scene, "bulk-replay", liveAuthority: false, path: _bulkPath);
            string outcome = $"Replayed {replayed.Summary.PagesRead}/{replayed.Summary.StoredPages} page(s), " +
                $"{replayed.Summary.RecordsSelected}/{replayed.Summary.RecordsRead} record(s) into scene " +
                $"{replayed.Scene.Identity.CaptureId}. Replay is offline-only and grants no live authority.";
            StatusMessage = outcome;
            return SuccessResult(correlation, "ReplayBulk", "ReplayBulk", outcome, started);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "Replay canceled; the previous input (if any) is unchanged.";
            return FailResult(correlation, "ReplayBulk", "Canceled.", started);
        }
        catch (Exception error)
        {
            StatusMessage = "Replay failed: " + error.Message;
            return FailResult(correlation, "ReplayBulk", "Failed: " + error.Message, started);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Opens a durable bulk-capture directory without contacting Allegro.
    /// Authentication failures and missing fragments are rejected; a
    /// partially authenticated directory never becomes a scene.
    /// </summary>
    public async Task<LaneAOperationResult> OpenBulkDirectoryAsync(
        string directory, CancellationToken cancellationToken = default)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        Guid correlation = Guid.NewGuid();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return FailResult(correlation, "OpenBulk", "No directory was provided.", started);
        }
        IsBusy = true;
        StatusMessage = "Authenticating the bulk-capture directory…";
        try
        {
            EngineBulkCapture opened = await EngineBulkCapture.OpenAsync(
                Path.GetFullPath(directory.Trim()), cancellationToken);
            await ReplaceBulkAsync(opened, Path.GetFullPath(directory.Trim()));
            StatusMessage = $"Opened bulk capture {opened.Info.Identity.CaptureToken}: " +
                $"{opened.Info.PageCount} page(s), complete coverage={opened.Info.HasCompleteCoverage}.";
            return SuccessResult(correlation, "OpenBulk", "OpenBulk", StatusMessage, started);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "Open canceled.";
            return FailResult(correlation, "OpenBulk", "Canceled.", started);
        }
        catch (Exception error)
        {
            StatusMessage = "Open failed: " + error.Message;
            return FailResult(correlation, "OpenBulk", "Failed: " + error.Message, started);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Exports the held bulk capture to a separately sealed directory.
    /// Existing destinations are rejected by Engine; the error is surfaced,
    /// never worked around.
    /// </summary>
    public async Task<LaneAOperationResult> ExportBulkAsync(
        string directory, CancellationToken cancellationToken = default)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        Guid correlation = Guid.NewGuid();
        ObjectDisposedException.ThrowIf(_disposed, this);
        EngineBulkCapture? bulk = _bulk;
        if (bulk is null)
        {
            return FailResult(correlation, "ExportBulk", "No bulk capture is held.", started);
        }
        if (string.IsNullOrWhiteSpace(directory))
        {
            return FailResult(correlation, "ExportBulk", "No destination directory was provided.", started);
        }
        IsBusy = true;
        StatusMessage = "Exporting the sealed bulk capture…";
        try
        {
            string destination = Path.GetFullPath(directory.Trim());
            await bulk.ExportAsync(destination, cancellationToken);
            StatusMessage = $"Bulk capture exported to {destination}.";
            return new LaneAOperationResult(
                correlation, "T08", "ExportBulk", "ExportBulk",
                _scene?.Identity.CaptureId, SourceProvenance, true, StatusMessage,
                started, DateTimeOffset.UtcNow,
                ImmutableArray<string>.Empty, destination,
                null);
        }
        catch (Exception error)
        {
            StatusMessage = "Export failed: " + error.Message;
            return FailResult(correlation, "ExportBulk", "Failed: " + error.Message, started);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Saves the attached scene as a versioned data-only archive. Saving
    /// never attaches native authority; loading the file back is offline.
    /// </summary>
    public async Task<LaneAOperationResult> SaveArchiveAsync(
        string path, bool overwrite, CancellationToken cancellationToken = default)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        Guid correlation = Guid.NewGuid();
        ObjectDisposedException.ThrowIf(_disposed, this);
        DesignScene? scene = _scene;
        if (scene is null)
        {
            return FailResult(correlation, "SaveArchive", "No scene is attached.", started);
        }
        if (string.IsNullOrWhiteSpace(path))
        {
            return FailResult(correlation, "SaveArchive", "No archive path was provided.", started);
        }
        IsBusy = true;
        StatusMessage = "Saving the scene archive…";
        try
        {
            string fullPath = Path.GetFullPath(path.Trim());
            if (File.Exists(fullPath) && !overwrite)
            {
                return FailResult(
                    correlation, "SaveArchive",
                    $"Refusing to overwrite existing archive '{fullPath}' without explicit approval.",
                    started);
            }
            await SceneArchive.SaveAsync(fullPath, scene, overwrite, cancellationToken);
            _sourcePath = fullPath;
            string sha256 = LaneAArtifacts.ComputeFileSha256(fullPath);
            StatusMessage = $"Archive saved to {fullPath} (sha256 {sha256}).";
            return new LaneAOperationResult(
                correlation, "T08", "SaveArchive", "SaveArchive",
                scene.Identity.CaptureId, SourceProvenance, true, StatusMessage,
                started, DateTimeOffset.UtcNow,
                ImmutableArray<string>.Empty, fullPath, sha256);
        }
        catch (Exception error)
        {
            StatusMessage = "Save failed: " + error.Message;
            return FailResult(correlation, "SaveArchive", "Failed: " + error.Message, started);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Loads a versioned archive offline. Loading never contacts Allegro,
    /// loads code, or creates editing authority; the scene attaches as
    /// offline-only. Corrupt, truncated, and unknown-schema inputs are
    /// rejected without a scene.
    /// </summary>
    public async Task<LaneAOperationResult> OpenArchiveAsync(
        string path, CancellationToken cancellationToken = default)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        Guid correlation = Guid.NewGuid();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(path))
        {
            return FailResult(correlation, "OpenArchive", "No archive path was provided.", started);
        }
        IsBusy = true;
        StatusMessage = "Loading the scene archive offline…";
        try
        {
            string fullPath = Path.GetFullPath(path.Trim());
            DesignScene loaded = await SceneArchive.LoadAsync(fullPath, cancellationToken);
            AttachSceneInternal(loaded, "archive", liveAuthority: false, path: fullPath);
            string outcome = $"Opened archive {fullPath} as offline-only scene {loaded.Identity.CaptureId}.";
            StatusMessage = outcome;
            return SuccessResult(correlation, "OpenArchive", "OpenArchive", outcome, started);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "Open canceled.";
            return FailResult(correlation, "OpenArchive", "Canceled.", started);
        }
        catch (Exception error)
        {
            StatusMessage = "Open failed: " + error.Message;
            return FailResult(correlation, "OpenArchive", "Failed: " + error.Message, started);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Attaches a caller-supplied scene (example or test fixture) as
    /// offline-only. Synthetic examples are labeled as such and never grant
    /// live authority.
    /// </summary>
    public LaneAOperationResult AttachOfflineScene(DesignScene scene, string kind, string? path = null)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        Guid correlation = Guid.NewGuid();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(scene);
        AttachSceneInternal(scene, string.IsNullOrWhiteSpace(kind) ? "offline" : kind.Trim(),
            liveAuthority: false, path: path);
        string outcome = $"Attached {SourceKind} scene {scene.Identity.CaptureId} (offline-only).";
        StatusMessage = outcome;
        return SuccessResult(correlation, "AttachOffline", "AttachOffline", outcome, started);
    }

    /// <summary>
    /// Explicit reacquisition for live work: issues a fresh native request
    /// through the host delegate. The new capture carries a new identity;
    /// when the identity is unexpectedly identical it is reported, not
    /// hidden.
    /// </summary>
    public async Task<LaneAOperationResult> ReacquireAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        Guid correlation = Guid.NewGuid();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_acquireScene is null)
        {
            return FailResult(
                correlation, "Reacquire",
                "Live reacquisition is unavailable in this context.", started);
        }
        Guid? previous = _scene?.Identity.CaptureId;
        IsBusy = true;
        StatusMessage = "Reacquiring a fresh capture for live work…";
        try
        {
            DesignScene fresh = await _acquireScene(BuildSceneQuery(), null, cancellationToken);
            AttachSceneInternal(fresh, "live", liveAuthority: true, path: null);
            string outcome = previous.HasValue && previous.Value == fresh.Identity.CaptureId
                ? $"Reacquired, but the capture identity is unchanged ({fresh.Identity.CaptureId}); " +
                  "the native state may not have changed since the previous capture."
                : $"Reacquired fresh capture {fresh.Identity.CaptureId} " +
                  (previous.HasValue ? $"(previous was {previous.Value})." : ".");
            StatusMessage = outcome;
            return SuccessResult(correlation, "Reacquire", "Reacquire", outcome, started);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "Reacquisition canceled; the previous input is unchanged.";
            return FailResult(correlation, "Reacquire", "Canceled.", started);
        }
        catch (Exception error)
        {
            StatusMessage = "Reacquisition failed: " + error.Message;
            return FailResult(correlation, "Reacquire", "Failed: " + error.Message, started);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Offline-supported analysis entry: runs a caller-supplied offline
    /// computation against the attached scene without any native authority.
    /// Native edit authority is never consulted here.
    /// </summary>
    public Task<LaneAOperationResult> RunOfflineQueryAsync(
        Func<DesignScene, CancellationToken, Task<string>> query,
        string queryName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(queryName);
        DateTimeOffset started = DateTimeOffset.UtcNow;
        Guid correlation = Guid.NewGuid();
        ObjectDisposedException.ThrowIf(_disposed, this);
        DesignScene? scene = _scene;
        if (scene is null)
        {
            return Task.FromResult(FailResult(correlation, queryName, "No scene is attached.", started));
        }
        IsBusy = true;
        StatusMessage = $"Running offline query '{queryName}'…";
        return Task.Run(async () =>
        {
            try
            {
                string summary = await query(scene, cancellationToken);
                StatusMessage = summary;
                return SuccessResult(correlation, queryName, queryName, summary, started);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                StatusMessage = "Offline query canceled.";
                return FailResult(correlation, queryName, "Canceled.", started);
            }
            catch (Exception error)
            {
                StatusMessage = "Offline query failed: " + error.Message;
                return FailResult(correlation, queryName, "Failed: " + error.Message, started);
            }
            finally
            {
                IsBusy = false;
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Exports a diagnostic manifest: capture identity, scope, coverage,
    /// schema versions, budgets, bulk info, and archive hash. Offline-safe.
    /// </summary>
    public async Task<LaneAOperationResult> ExportManifestAsync(
        string path, bool overwrite, CancellationToken cancellationToken = default)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        Guid correlation = Guid.NewGuid();
        ObjectDisposedException.ThrowIf(_disposed, this);
        DesignScene? scene = _scene;
        if (scene is null)
        {
            return FailResult(correlation, "ExportManifest", "No scene is attached.", started);
        }
        var manifest = new CapturedSceneManifest(
            "t08-captured-scene",
            1,
            started,
            scene.Identity.CaptureId.ToString(),
            scene.Identity.CapturedAt,
            SourceProvenance,
            _sourceKind,
            _liveAuthority,
            _sourcePath,
            scene.Query.Families.ToArray(),
            scene.Query.Layers.Select(layer => layer.Value).ToArray(),
            scene.Query.IncludeContours,
            scene.Query.MaximumObjects,
            _maximumContourVertices,
            CoverageSummary,
            _bulk?.Info,
            _sourcePath is not null && File.Exists(_sourcePath)
                ? LaneAArtifacts.ComputeFileSha256(_sourcePath)
                : null);
        try
        {
            (string written, string sha256) = await LaneAArtifacts.WriteJsonAsync(
                path, manifest, overwrite, cancellationToken);
            StatusMessage = $"Manifest exported to {written} (sha256 {sha256}).";
            return new LaneAOperationResult(
                correlation, "T08", "ExportManifest", "ExportManifest",
                scene.Identity.CaptureId, SourceProvenance, true,
                $"Manifest written to {written}.",
                started, DateTimeOffset.UtcNow,
                ImmutableArray<string>.Empty, written, sha256);
        }
        catch (Exception error)
        {
            StatusMessage = "Manifest export failed: " + error.Message;
            return FailResult(correlation, "ExportManifest", "Failed: " + error.Message, started);
        }
    }

    /// <summary>
    /// Closes the attached input and releases owned readers, files, decoded
    /// caches, and bulk storage. Exported archives on disk are preserved.
    /// </summary>
    public async Task<LaneAOperationResult> CloseAsync()
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        Guid correlation = Guid.NewGuid();
        await ReplaceBulkAsync(null, null);
        _scene = null;
        _sourceKind = "none";
        _liveAuthority = false;
        _sourcePath = null;
        _progressDiagnostics.Clear();
        RefreshCoverage();
        Raise(nameof(HasScene));
        Raise(nameof(SourceKind));
        Raise(nameof(SourceCaptureId));
        Raise(nameof(SourceProvenance));
        Raise(nameof(SourcePath));
        Raise(nameof(LiveAuthority));
        Raise(nameof(LiveAuthorityText));
        Raise(nameof(BulkInfo));
        Raise(nameof(BulkInfoText));
        StatusMessage = "Input closed and owned resources released; exported archives are preserved.";
        return SuccessResult(correlation, "Close", "Close", StatusMessage, started);
    }

    private void AttachSceneInternal(DesignScene scene, string kind, bool liveAuthority, string? path)
    {
        _scene = scene;
        _sourceKind = kind;
        _liveAuthority = liveAuthority;
        _sourcePath = path;
        RefreshCoverage();
        Raise(nameof(HasScene));
        Raise(nameof(SourceKind));
        Raise(nameof(SourceCaptureId));
        Raise(nameof(SourceProvenance));
        Raise(nameof(SourcePath));
        Raise(nameof(LiveAuthority));
        Raise(nameof(LiveAuthorityText));
        Raise(nameof(BulkInfoText));
    }

    private void RefreshCoverage()
    {
        if (_scene is null)
        {
            CoverageSummary = "No input attached.";
            return;
        }
        int complete = 0;
        int total = 0;
        var notes = new List<string>();
        foreach (DataFamily family in Enum.GetValues<DataFamily>())
        {
            FamilyCoverage coverage = _scene.Coverage[family];
            total++;
            if (coverage.IsComplete)
            {
                complete++;
            }
            else if (coverage.Availability != DataAvailability.NotRequested)
            {
                notes.Add($"{family}: {coverage.Availability}/{coverage.Completeness}.");
            }
        }
        CoverageSummary =
            $"Coverage: {complete}/{total} families complete. " + string.Join(" ", notes) +
            (_liveAuthority
                ? " Live authority: current session capture."
                : " Live authority: none (offline-only input).");
    }

    private async Task ReplaceBulkAsync(EngineBulkCapture? next, string? path)
    {
        EngineBulkCapture? previous = Interlocked.Exchange(ref _bulk, next);
        _bulkPath = path;
        if (previous is not null)
        {
            try
            {
                await previous.DisposeAsync();
            }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "Captured scenes could not fully dispose its bulk capture: {0}", error.Message);
            }
        }
        Raise(nameof(BulkInfo));
        Raise(nameof(BulkInfoText));
    }

    private LaneAOperationResult SuccessResult(
        Guid correlation, string requested, string executed, string outcome, DateTimeOffset started) =>
        new(correlation, "T08", requested, executed,
            _scene?.Identity.CaptureId, SourceProvenance, true, outcome,
            started, DateTimeOffset.UtcNow,
            ImmutableArray<string>.Empty, null, null);

    private LaneAOperationResult FailResult(
        Guid correlation, string action, string reason, DateTimeOffset started) =>
        new(correlation, "T08", action, action + " (rejected)",
            _scene?.Identity.CaptureId, SourceProvenance, false, reason,
            started, DateTimeOffset.UtcNow,
            ImmutableArray<string>.Empty, null, null);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await ReplaceBulkAsync(null, null);
        _scene = null;
    }
}

/// <summary>
/// Diagnostic manifest for one captured scene. Hashes detect corruption, not
/// authenticity; the manifest itself is application-written evidence, never
/// proof that Allegro generated the outputs.
/// </summary>
public sealed record CapturedSceneManifest(
    string ToolId,
    int ManifestVersion,
    DateTimeOffset ExportedAt,
    string CaptureId,
    DateTimeOffset CapturedAt,
    string Provenance,
    string SourceKind,
    bool LiveAuthority,
    string? SourcePath,
    DataFamily[] QueryFamilies,
    string[] QueryLayers,
    bool QueryIncludeContours,
    int QueryMaximumObjects,
    int MaximumContourVertices,
    string CoverageSummary,
    EngineBulkCaptureInfo? BulkInfo,
    string? ArchiveSha256);
