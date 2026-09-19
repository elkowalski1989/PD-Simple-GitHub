using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CircuitHub.AllegroBridge.Engine.Drawing;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Wpf;
using CircuitHub.AllegroBridge.Wpf.Engine;
using PD.Simple.Diagnostics;

namespace PD.Simple.Corridor;

/// <summary>
/// Owns DPVC setup, findings, review decisions, and workflow. The caller supplies
/// one Engine session and its same-session WPF presentation; this view model owns
/// and disposes neither shared object.
/// </summary>
public sealed class DpViaCorridorWorkspaceViewModel :
    INotifyPropertyChanged,
    IDisposable
{
    private readonly AllegroEngineSession _session;
    private readonly EngineWpfPresentation _presentation;
    private readonly IDpViaCorridorService _corridor;
    private readonly BoardOverlayController _boardOverlay;
    private readonly OverlayDebugCapture _debugCapture;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly RelayCommand _runCommand;
    private readonly RelayCommand _openReportCommand;
    private readonly RelayCommand _zoomCommand;
    private readonly ObservableCollection<DpViaCorridorFinding> _visibleFindings = [];
    private EngineSessionSnapshot _state;
    private CancellationTokenSource? _overlayUpdate;
    private readonly DpViaCorridorSelectionPipeline _pipeline = new();
    private long _lastFollowTriggerTicks;
    private readonly System.Collections.Generic.List<DpViaCorridorSelectionTiming> _recentSelectionTimings = new();
    private string _marginMils = "0";
    private string _moduleFilter = string.Empty;
    private bool _includeUnused;
    private string _searchText = string.Empty;
    private string _riskFilter = "All risks";
    private string _statusTitle = "Connect to Allegro";
    private string _statusDetail =
        "Open PD from Allegro with a board loaded to run this check.";
    private bool _isBusy;
    private bool _settingsChanged;
    private bool _hasProblem;
    private string _requestReport = string.Empty;
    private DpViaCorridorAnalysis? _analysis;
    private DpViaCorridorFinding? _selectedFinding;
    private bool _followSelection = true;
    private bool _isNavigating;
    private bool _disposed;
    private string _navigationError = string.Empty;
    private DpViaCorridorReviewCapture? _capturedReview;
    private DpViaCorridorZoomResult? _verifiedZoom;
    private bool _showHighlighting = true;
    private bool _workspaceVisible;
    private string _boardOverlayError = string.Empty;
    private long _drawingPublishedEpoch = -1;

    private DpViaCorridorResult? CurrentResult => _analysis?.Result;

    private sealed record NavigationContext(
        long Epoch,
        WorkspaceDocumentIdentity Document,
        Guid CaptureId,
        string Report,
        string FindingId,
        string Layer);

    internal sealed record DpViaCorridorSelectionTiming(
        long Epoch,
        string FindingId,
        long NavigateMilliseconds,
        long CaptureMilliseconds,
        DpViaCorridorNavigationPhases? NavigationPhases,
        long? OverlayMilliseconds = null);

    public DpViaCorridorWorkspaceViewModel(
        AllegroEngineSession session,
        EngineWpfPresentation presentation,
        Func<Window?>? debugWindowProvider = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
        if (!ReferenceEquals(_presentation.Session, _session))
        {
            throw new ArgumentException(
                "The corridor workspace requires a WPF presentation attached " +
                "to the supplied Engine session.",
                nameof(presentation));
        }

        _corridor = new EngineDpViaCorridorService(_session);
        _boardOverlay = new BoardOverlayController(_session, _presentation);
        _debugCapture = new OverlayDebugCapture(debugWindowProvider);
        _dispatcher = Dispatcher.CurrentDispatcher;
        _state = _session.State;
        _runCommand = new RelayCommand(Run, () => CanRun);
        _openReportCommand = new RelayCommand(OpenReport, () => CanOpenReport);
        _zoomCommand = new RelayCommand(() => ZoomSelectedFinding(), () => CanZoom);
        ClearFiltersCommand = new RelayCommand(() =>
        {
            SearchText = string.Empty;
            RiskFilter = "All risks";
        });
        VisibleFindings =
            new ReadOnlyObservableCollection<DpViaCorridorFinding>(_visibleFindings);
        _session.StateChanged += Session_StateChanged;
        _presentation.StateChanged += Presentation_StateChanged;
        _presentation.OverlayStateChanged += Presentation_OverlayStateChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand RunCommand => _runCommand;

    public ICommand OpenReportCommand => _openReportCommand;

    public ICommand ZoomCommand => _zoomCommand;

    public ICommand ClearFiltersCommand { get; }

    public IReadOnlyList<string> RiskOptions { get; } =
        ["All risks", "CRITICAL", "MEDIUM", "LOW"];

    public ReadOnlyObservableCollection<DpViaCorridorFinding> VisibleFindings { get; }

    public string MarginMils
    {
        get => _marginMils;
        set
        {
            if (Set(ref _marginMils, value))
            {
                InputsChanged();
            }
        }
    }

    public string ModuleFilter
    {
        get => _moduleFilter;
        set
        {
            if (Set(ref _moduleFilter, value))
            {
                InputsChanged();
            }
        }
    }

    public bool IncludeUnused
    {
        get => _includeUnused;
        set
        {
            if (Set(ref _includeUnused, value))
            {
                InputsChanged();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value))
            {
                RefreshFindings();
            }
        }
    }

    public string RiskFilter
    {
        get => _riskFilter;
        set
        {
            if (RiskOptions.Contains(value) && Set(ref _riskFilter, value))
            {
                RefreshFindings();
            }
        }
    }

    public DpViaCorridorFinding? SelectedFinding
    {
        get => _selectedFinding;
        set
        {
            if (value is not null && !_visibleFindings.Contains(value))
            {
                return;
            }

            if (Set(ref _selectedFinding, value))
            {
                SupersedeSelection();
                Changed(nameof(SelectedFindingTitle));
                Changed(nameof(SelectedFindingDetail));
                NotifyState();
                if (_followSelection)
                {
                    FollowSelectedFinding();
                }
            }
        }
    }

    public DpViaCorridorResult? Result => CurrentResult;

    public bool HasResult => CurrentResult is not null;

    public bool HasVisibleFindings => _visibleFindings.Count > 0;

    public bool IsBusy => _isBusy;

    public bool IsInputEnabled => !_isBusy && !_isNavigating;

    public bool IsNavigating => _isNavigating;

    public bool FollowSelection
    {
        get => _followSelection;
        set
        {
            if (Set(ref _followSelection, value) && value)
            {
                FollowSelectedFinding();
            }
        }
    }

    internal Func<DpViaCorridorAnalysis, DpViaCorridorFinding, CancellationToken, Task<DpViaCorridorZoomResult>>? NavigateOverride { get; set; }

    internal Func<LiveDesignScene, DrawingScene, CancellationToken, Task<AllegroReviewFrame>>? CaptureReviewOverride { get; set; }

    internal int FollowAttemptCountForTest { get; set; }

    internal void AdoptResultForTest(DpViaCorridorAnalysis analysis) =>
        AdoptResult(analysis ?? throw new ArgumentNullException(nameof(analysis)));

    internal System.Collections.Generic.IReadOnlyList<DpViaCorridorSelectionTiming> RecentSelectionTimings =>
        _recentSelectionTimings.ToArray();

    internal long? LastOverlayPresentMilliseconds { get; private set; }

    public DpViaCorridorReviewCapture? CapturedReview => _capturedReview;

    public DpViaCorridorZoomResult? VerifiedZoom => _verifiedZoom;

    public bool HasCapturedReview => _capturedReview is not null;

    public bool ShowHighlighting
    {
        get => _showHighlighting;
        set
        {
            if (Set(ref _showHighlighting, value))
            {
                UpdateBoardOverlay();
                Changed(nameof(BoardOverlayStatus));
            }
        }
    }

    public string BoardOverlayStatus
    {
        get
        {
            if (_boardOverlayError.Length > 0)
            {
                return _boardOverlayError;
            }
            if (!ShowHighlighting)
            {
                return "Highlighting off · review and PNG export use raw captured pixels.";
            }
            if (!TryGetCurrentOverlayIdentity(
                    out WorkspaceDocumentIdentity? document,
                    out Guid captureId,
                    out long revision))
            {
                return "Select a captured crossing to publish its Engine/WPF overlay.";
            }

            EngineWpfOverlayState state = _presentation.OverlayState;
            bool exactRevision =
                state.Document == document &&
                state.CaptureId == captureId &&
                state.Revision == revision;
            if (!exactRevision)
            {
                return "Waiting to publish the selected captured finding in Allegro.";
            }
            return state.Availability switch
            {
                EngineWpfOverlayAvailability.Visible =>
                    "Shown through the Engine/WPF overlay · captured finding; re-run after edits.",
                EngineWpfOverlayAvailability.Pending =>
                    "Publishing the selected captured finding in Allegro…",
                EngineWpfOverlayAvailability.Unavailable =>
                    "Allegro overlay unavailable: " +
                    (state.UnavailableReason ?? "No current overlay pixels were published."),
                _ => "The selected captured finding is not currently shown in Allegro.",
            };
        }
    }

    internal void SetWorkspaceVisible(bool visible)
    {
        if (_workspaceVisible == visible)
        {
            return;
        }

        _workspaceVisible = visible;
        UpdateBoardOverlay();
        Changed(nameof(BoardOverlayStatus));
    }

    public string NavigationError => _navigationError;

    public bool HasNavigationError => _navigationError.Length > 0;

    public bool CanZoom =>
        !_disposed &&
        !_isBusy &&
        !_isNavigating &&
        HasReadySession &&
        HasCapability(EngineCapabilities.Display) &&
        HasCapability(EngineCapabilities.Presentation) &&
        _presentation.State.IsAvailable &&
        IsResultCurrent &&
        _selectedFinding is not null;

    public string ZoomAvailability
    {
        get
        {
            if (_disposed)
            {
                return "Zoom unavailable: this corridor workspace is closed.";
            }
            if (_isBusy)
            {
                return "Zoom unavailable while the corridor check is running.";
            }
            if (_isNavigating)
            {
                return "Zoom and review capture are already running.";
            }
            if (!HasReadySession)
            {
                return "Zoom unavailable: connect to one current Allegro board.";
            }
            if (!HasCapability(EngineCapabilities.Display))
            {
                return "Zoom unavailable: " +
                    (CapabilityUnavailableReason(EngineCapabilities.Display) ??
                        "Engine did not provide native display authority.");
            }
            if (!HasCapability(EngineCapabilities.Presentation))
            {
                return "Review capture unavailable: " +
                    (CapabilityUnavailableReason(EngineCapabilities.Presentation) ??
                        "Engine did not provide presentation authority.");
            }
            if (!_presentation.State.IsAvailable)
            {
                return "Review capture unavailable: " +
                    (_presentation.State.UnavailableReason ??
                        "the Engine/WPF presentation is unavailable.");
            }
            if (!IsResultCurrent)
            {
                return "Run a current corridor check before navigating in Allegro.";
            }
            if (_selectedFinding is null)
            {
                return "Select a crossing to capture its current Allegro review.";
            }
            return "Zoom to the selected crossing, capture its Allegro view, and show the review overlay.";
        }
    }

    public string PreviewTitle =>
        HasCapturedReview
            ? "Captured Allegro review"
            : "Measured corridor geometry";

    public string PreviewStatus =>
        _navigationError.Length > 0
            ? _navigationError
            : _isNavigating
                ? "Zooming through Engine and capturing the current WPF review…"
                : _capturedReview is { } capture
                    ? $"Captured {capture.CapturedAt.LocalDateTime:HH:mm:ss} · " +
                        $"selected finding layer: {capture.Layer}. The canonical " +
                        "drawing marks the measured corridor, via centers, and " +
                        "intrusion—not copper outlines. Wheel to magnify; Fit to reset."
                    : ZoomAvailability;

    public bool HasProblem => _hasProblem;

    public bool IsResultCurrent =>
        CurrentResult is not null &&
        !_settingsChanged &&
        _state.ConnectionState == EngineConnectionState.Ready &&
        _state.Document is { } document &&
        CurrentResult.BoardGeneration == document.BoardGeneration &&
        _analysis!.IsCurrentFor(document) &&
        _analysis.LiveScene?.IsCurrent == true;

    public bool CanRun =>
        !_disposed &&
        !_isBusy &&
        !_isNavigating &&
        HasReadySession &&
        HasCapability(EngineCapabilities.SceneRead) &&
        InputError.Length == 0;

    public bool CanOpenReport =>
        CurrentResult is not null &&
        !_isBusy &&
        string.Equals(
            CurrentResult.ReportPath,
            _requestReport,
            StringComparison.OrdinalIgnoreCase) &&
        File.Exists(_requestReport);

    public string InputError =>
        !TryMargin(out _)
            ? "Enter a margin from 0 to 50 mils."
            : ModuleFilter.Length > 64 || ModuleFilter.Any(char.IsControl)
                ? "Use an Engine module instance name of up to 64 characters."
                : string.Empty;

    public string BoardDisplay =>
        HasReadySession
            ? _state.Document?.Design ?? "Current PCB"
            : "No live board";

    public string StatusTitle => _statusTitle;

    public string StatusDetail => _statusDetail;

    public string RunLabel =>
        _isBusy
            ? "Checking in Allegro…"
            : HasResult
                ? "Run again"
                : "Run corridor check";

    public string RunAvailability =>
        InputError.Length > 0
            ? InputError
            : _isNavigating
                ? "Wait for Engine navigation and review capture to finish."
                : _isBusy
                    ? "Keep the board open. This checker has no mid-run cancellation."
                    : !HasReadySession
                        ? "A connected Allegro Engine board is required."
                        : CapabilityUnavailableReason(EngineCapabilities.SceneRead) ??
                            "Reads one coherent Engine scene and screens it in the reusable C# tool module.";

    public string PairCountDisplay =>
        CurrentResult?.PairCount.ToString("N0") ?? "—";

    public string CorridorCountDisplay =>
        CurrentResult?.CorridorCount.ToString("N0") ?? "—";

    public string FindingCountDisplay =>
        CurrentResult?.FindingCount.ToString("N0") ?? "—";

    public string RiskSummary =>
        CurrentResult is null
            ? "Risk labels are advisory, not an SI sign-off."
            : $"{CurrentResult.CriticalCount:N0} critical · " +
                $"{CurrentResult.MediumCount:N0} medium · " +
                $"{CurrentResult.LowCount:N0} low";

    public string ResultProvenance =>
        CurrentResult is null
            ? "Illustration only · no board results yet"
            : $"{(IsResultCurrent ? "Captured result" : "Previous result")}" +
                $" · generation {CurrentResult.BoardGeneration} · " +
                $"source {CurrentResult.SourceUnits} · display mils";

    public string ResultScope =>
        CurrentResult is null
            ? "Results will appear here after analysis."
            : _settingsChanged
                ? "Settings changed. Run again to analyze with these options."
                : !IsResultCurrent
                    ? "The Engine document changed. These results are historical; run again."
                    : !CurrentResult.HasCompleteInputs
                        ? "Incomplete Engine inputs. Findings are review information only; " +
                            "a clear result cannot be established. See the report."
                        : CurrentResult.Truncated
                            ? $"Showing a bounded capture of {CurrentResult.Findings.Count:N0} " +
                                $"of {CurrentResult.FindingCount:N0} crossings. " +
                                "The report contains the full analysis."
                            : "Captured analysis, not a live board view. " +
                                "Risk classifications are advisory.";

    public string FindingListSummary =>
        CurrentResult is null
            ? "No analysis yet"
            : $"{_visibleFindings.Count:N0} shown · " +
                $"{CurrentResult.FindingCount:N0} total";

    public string EmptyResultsTitle =>
        CurrentResult is null
            ? "Run a check to review crossings"
            : !CurrentResult.HasCompleteInputs
                ? "Review required: incomplete inputs"
                : CurrentResult.FindingCount == 0
                    ? "No crossings reported by screening"
                    : "No matching crossings";

    public string EmptyResultsDetail =>
        CurrentResult is null
            ? "The checker will return the affected pair, aggressor, layer, " +
                "and captured geometry."
            : !CurrentResult.HasCompleteInputs
                ? "Required Engine data was unavailable. The absence of displayed " +
                    "crossings is not a pass; read the coverage warnings in the report."
                : CurrentResult.FindingCount == 0
                    ? "This run found no corridor crossings in its analyzed scope. " +
                        "This is not a full SI sign-off."
                    : "Change the search or risk filter to see other captured crossings.";

    public string SelectedFindingTitle =>
        _selectedFinding?.PairName ?? "What the checker looks for";

    public string SelectedFindingDetail =>
        _selectedFinding is null
            ? "Foreign conductors entering the protected corridor between P and N via centers."
            : $"{_selectedFinding.AggressorNet} · " +
                $"{_selectedFinding.ObjectType} · {_selectedFinding.Layer}" +
                SelectionTimingSuffix(_selectedFinding.Id);

    private string SelectionTimingSuffix(string findingId)
    {
        for (int index = _recentSelectionTimings.Count - 1; index >= 0; --index)
        {
            DpViaCorridorSelectionTiming timing = _recentSelectionTimings[index];
            if (timing.FindingId == findingId && timing.OverlayMilliseconds is not null)
            {
                string phases = timing.NavigationPhases is null
                    ? "phases unavailable"
                    : $"region {timing.NavigationPhases.RegionMilliseconds} ms; " +
                        $"validate {timing.NavigationPhases.WitnessValidationMilliseconds} ms; " +
                        $"zoom {timing.NavigationPhases.ZoomMilliseconds} ms";
                return $" · selection {timing.Epoch}: navigate {timing.NavigateMilliseconds} ms; " +
                    $"capture {timing.CaptureMilliseconds} ms; overlay {timing.OverlayMilliseconds} ms; " +
                    phases;
            }
        }
        return string.Empty;
    }

    private bool HasReadySession =>
        _state.ConnectionState == EngineConnectionState.Ready &&
        _state.Document is not null &&
        _session.Workspace.IsConnected;

    private bool HasCapability(EngineCapabilityId capabilityId) =>
        _state.Capabilities.Supports(capabilityId);

    private string? CapabilityUnavailableReason(EngineCapabilityId capabilityId)
    {
        EngineCapability? capability = _state.Capabilities.Items
            .FirstOrDefault(item => item.Id == capabilityId);
        if (capability is null)
        {
            return $"Engine capability '{capabilityId.Value}' was not reported.";
        }
        return capability.Availability == EngineCapabilityAvailability.Available
            ? null
            : capability.UnavailableReason ??
                string.Join(
                    " ",
                    capability.Diagnostics.Select(static diagnostic => diagnostic.Message));
    }

    private bool TryMargin(out decimal margin) =>
        decimal.TryParse(
            MarginMils,
            NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out margin) &&
        margin is >= 0 and <= 50;

    private void InputsChanged()
    {
        _settingsChanged = CurrentResult is not null;
        InvalidateCapturedReview();
        NotifyState();
    }

    private void SupersedeSelection()
    {
        _pipeline.BeginSelection();
        CancelOverlayUpdate();
        _capturedReview = null;
        _verifiedZoom = null;
        _drawingPublishedEpoch = -1;
        _navigationError = string.Empty;
    }

    private void InvalidateCapturedReview()
    {
        SupersedeSelection();
        _boardOverlay.Clear();
        _boardOverlayError = string.Empty;
    }

    private void UpdateBoardOverlay()
    {
        CancelOverlayUpdate();
        _boardOverlayError = string.Empty;
        if (_disposed ||
            !_workspaceVisible ||
            !ShowHighlighting ||
            _capturedReview is not { } capture ||
            _verifiedZoom is not { } zoom ||
            _selectedFinding is not { } finding ||
            _analysis?.LiveScene is not { } source ||
            !IsResultCurrent)
        {
            _boardOverlay.Clear();
            return;
        }

        PublishOverlayCore(
            zoom,
            finding,
            source,
            capture.DrawingSource.ForLive(source.Scene),
            capture.DrawingSource.Drawings.Revision,
            capture,
            _pipeline.CurrentEpoch);
    }

    /// <summary>
    /// Fix 1 drawing-first publication: publishes the verified-zoom Engine
    /// drawing immediately, without requiring review pixels. Review capture
    /// still runs through the pipeline and re-publishes with the review
    /// when it lands; sequence fencing keeps a late drawing presenter from
    /// overwriting that newer capture-bound publication.
    /// </summary>
    private void PublishVerifiedDrawing(
        DpViaCorridorZoomResult zoom,
        DpViaCorridorFinding finding,
        LiveDesignScene source,
        long epoch)
    {
        CancelOverlayUpdate();
        _boardOverlayError = string.Empty;
        if (_disposed ||
            !_workspaceVisible ||
            !ShowHighlighting ||
            !IsResultCurrent)
        {
            _boardOverlay.Clear();
            return;
        }

        DpViaCorridorDrawingSource drawingSource =
            BoardOverlayDrawingPolicy.CorridorSource(
                source.Scene,
                finding,
                epoch);
        _drawingPublishedEpoch = epoch;
        PublishOverlayCore(
            zoom,
            finding,
            source,
            drawingSource.ForLive(source.Scene),
            drawingSource.Drawings.Revision,
            null,
            epoch);
    }

    private void PublishOverlayCore(
        DpViaCorridorZoomResult zoom,
        DpViaCorridorFinding finding,
        LiveDesignScene source,
        DrawingScene drawings,
        long revision,
        DpViaCorridorReviewCapture? capture,
        long epoch)
    {
        var overlay = new DpViaCorridorBoardOverlay(
            zoom,
            finding,
            source,
            drawings,
            revision);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token);
        _overlayUpdate = cancellation;
        _ = PresentBoardOverlayAsync(
            overlay,
            capture,
            epoch,
            cancellation);
    }

    private async Task PresentBoardOverlayAsync(
        DpViaCorridorBoardOverlay overlay,
        DpViaCorridorReviewCapture? capture,
        long epoch,
        CancellationTokenSource cancellation)
    {
        // Publication request order is owned by the WPF presentation: the
        // synchronous call below sequences this request before the await, so
        // the sampled value is this request's order under PD's single-issuer
        // discipline, the same affinity the retired PD counter assumed.
        long requested = -1;
        try
        {
            System.Diagnostics.Stopwatch overlayTimer = System.Diagnostics.Stopwatch.StartNew();
            ValueTask<EngineWpfPublicationReceipt> present =
                _boardOverlay.PresentAsync(overlay, cancellation.Token);
            requested = _presentation.CurrentSequence;
            EngineWpfPublicationReceipt receipt = await present;
            overlayTimer.Stop();
            LastOverlayPresentMilliseconds = overlayTimer.ElapsedMilliseconds;
            StampOverlayTiming(epoch, overlayTimer.ElapsedMilliseconds);
            if (IsOverlayContextCurrent(capture, epoch, receipt.Sequence))
            {
                AdoptOverlayState(_presentation.OverlayState);
                NotifyState();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error) when (
            error is InvalidOperationException or
                ArgumentException or
                NotSupportedException)
        {
            // A request that failed before the presentation sequenced it
            // reports against the current context; nothing newer could have
            // been issued by this single issuer in between.
            if (requested == -1 || IsOverlayContextCurrent(capture, epoch, requested))
            {
                _boardOverlay.Clear();
                _boardOverlayError =
                    "Allegro overlay unavailable: " + error.Message;
                NotifyState();
            }
        }
        finally
        {
            if (ReferenceEquals(_overlayUpdate, cancellation))
            {
                _overlayUpdate = null;
            }
            cancellation.Dispose();
        }
    }

    private bool IsOverlayContextCurrent(
        DpViaCorridorReviewCapture? capture,
        long epoch,
        long publishedSequence) =>
        !_disposed &&
        epoch == _pipeline.CurrentEpoch &&
        publishedSequence == _presentation.CurrentSequence &&
        (capture is null || ReferenceEquals(_capturedReview, capture)) &&
        _workspaceVisible &&
        ShowHighlighting &&
        IsResultCurrent;

    private void CancelOverlayUpdate()
    {
        CancellationTokenSource? cancellation = _overlayUpdate;
        _overlayUpdate = null;
        if (cancellation is null)
        {
            return;
        }
        cancellation.Cancel();
        // The in-flight presenter owns disposal in its finally block. Keeping
        // that single owner also leaves its cancellation filter safe to read.
    }

    private void FollowSelectedFinding()
    {
        FollowAttemptCountForTest++;
        if (_followSelection &&
            CanZoom &&
            !_isNavigating &&
            _capturedReview is null &&
            _navigationError.Length == 0)
        {
            _lastFollowTriggerTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            ZoomSelectedFinding(awaitQuietPeriod: true);
        }
    }

    private async void ZoomSelectedFinding(bool awaitQuietPeriod = false)
    {
        if (!CanZoom ||
            _analysis?.LiveScene is not { } source ||
            _selectedFinding is not { } finding)
        {
            return;
        }

        DpViaCorridorAnalysis analysis = _analysis;
        SupersedeSelection();
        var context = new NavigationContext(
            _pipeline.CurrentEpoch,
            analysis.Document,
            source.Scene.Identity.CaptureId,
            analysis.Result.ReportPath,
            finding.Id,
            finding.Layer);
        _isNavigating = true;
        NotifyState();
        Task<T> DispatchWhenNativeIdle<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken token) =>
            EngineNativeRetry.ExecuteWhenIdleAsync(
                operation,
                () => IsNavigationCurrent(context),
                cancellationToken: token);
        TimeSpan quietPeriod = TimeSpan.Zero;
        if (awaitQuietPeriod)
        {
            // Coalesce rapid reselection bursts: only a selection that stays
            // current through a short quiet period dispatches native work, so
            // superseded selections never start overlapping SKILL operations.
            long elapsedTicks = System.Diagnostics.Stopwatch.GetTimestamp() - _lastFollowTriggerTicks;
            long quietTicks = (long)(System.Diagnostics.Stopwatch.Frequency * 0.3);
            if (elapsedTicks < quietTicks)
            {
                quietPeriod = TimeSpan.FromSeconds(
                    (quietTicks - elapsedTicks) / (double)System.Diagnostics.Stopwatch.Frequency);
            }
        }
        var operations = new DpViaCorridorSelectionOperations(
            NavigateAsync: async (selection, token) =>
            {
                if (NavigateOverride is not null)
                {
                    return await NavigateOverride(analysis, finding, token);
                }
                return await DispatchWhenNativeIdle(
                    dispatch => _corridor.NavigateAsync(analysis, finding, dispatch),
                    token);
            },
            PublishDrawingAsync: (selection, zoom, navigateMilliseconds, token) =>
            {
                PublishVerifiedDrawing(zoom, finding, source, selection.Epoch);
                return Task.CompletedTask;
            },
            CaptureAsync: async (selection, token) =>
            {
                DpViaCorridorDrawingSource drawingSource =
                    BoardOverlayDrawingPolicy.CorridorSource(
                        source.Scene,
                        finding,
                        selection.Epoch);
                DrawingScene drawings = drawingSource.ForReview(source.Scene);
                if (CaptureReviewOverride is not null)
                {
                    return await CaptureReviewOverride(source, drawings, token);
                }
                return await DispatchWhenNativeIdle(
                    async dispatch => await _presentation.CaptureReviewAsync(
                        source,
                        drawings,
                        dispatch),
                    token);
            },
            PublishAsync: (selection, zoom, review, navigateMilliseconds, captureMilliseconds, token) =>
            {
                DpViaCorridorReviewCapture capture =
                    DpViaCorridorReviewCapture.Create(
                        review,
                        source,
                        BoardOverlayDrawingPolicy.CorridorSource(
                            source.Scene,
                            finding,
                            selection.Epoch),
                        zoom);
                _verifiedZoom = zoom;
                _capturedReview = capture;
                _navigationError = string.Empty;
                RetainSelectionTiming(new DpViaCorridorSelectionTiming(
                    selection.Epoch,
                    finding.Id,
                    navigateMilliseconds,
                    captureMilliseconds,
                    _corridor.LastNavigationPhases));
                UpdateBoardOverlay();
                return Task.CompletedTask;
            },
            IsCurrent: selection =>
                selection.Epoch == context.Epoch && IsNavigationCurrent(context));
        try
        {
            await _pipeline.RunSelectionAsync(
                context.Epoch,
                analysis,
                finding,
                operations,
                quietPeriod,
                _lifetime.Token);
        }
        catch (Exception error)
        {
            if (IsNavigationCurrent(context))
            {
                // Fix 1: when the verified-zoom drawing is already live for
                // this selection, a failed review capture must not take the
                // drawing down with it; only the review is reported missing.
                if (_drawingPublishedEpoch != context.Epoch)
                {
                    _boardOverlay.Clear();
                }
                _navigationError =
                    "Captured review unavailable: " + error.Message;
            }
        }
        finally
        {
            _isNavigating = false;
            if (!_disposed)
            {
                NotifyState();
                if (context.Epoch != _pipeline.CurrentEpoch)
                {
                    FollowSelectedFinding();
                }
            }
        }
    }

    private bool IsNavigationCurrent(NavigationContext context) =>
        !_disposed &&
        context.Epoch == _pipeline.CurrentEpoch &&
        IsResultCurrent &&
        _state.Document == context.Document &&
        _analysis?.LiveScene?.Scene.Identity.CaptureId == context.CaptureId &&
        CurrentResult?.ReportPath == context.Report &&
        _selectedFinding?.Id == context.FindingId &&
        _selectedFinding.Layer == context.Layer;

    private async void Run()
    {
        if (!CanRun || !TryMargin(out decimal margin))
        {
            return;
        }

        bool dispatched = false;
        try
        {
            InvalidateCapturedReview();
            string reportDirectory = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "PD-Simple",
                "Reports",
                "DPVC");
            Directory.CreateDirectory(reportDirectory);
            _requestReport = Path.Combine(
                reportDirectory,
                $"dpvc-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.rpt");
            _isBusy = true;
            _hasProblem = false;
            _settingsChanged = true;
            _statusTitle = "Checking in Allegro";
            _statusDetail =
                "Engine is collecting one coherent board scene; managed screening " +
                "follows. Missing required data remains explicit.";
            NotifyState();
            dispatched = true;
            DpViaCorridorAnalysis analysis = await _corridor.AnalyzeAsync(
                new DpViaCorridorOptions(
                    margin,
                    ModuleFilter.Trim(),
                    IncludeUnused),
                _requestReport,
                _lifetime.Token);
            if (_disposed)
            {
                return;
            }
            // The live document identity carries the native design path while
            // the scene/result carries its display name. Those names describe
            // the same captured board but are not required to be textually
            // identical; freshness is established by the full Engine identity.
            if (!analysis.IsCurrentFor(_state.Document))
            {
                throw new InvalidDataException(
                    "The Engine document changed during analysis. " +
                    "Run again on the current board.");
            }

            _isBusy = false;
            AdoptResult(analysis);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!_disposed)
            {
                string title = !dispatched
                    ? "Could not start check"
                    : error is InvalidDataException
                        ? "Result could not be verified"
                        : "Check did not complete";
                Fail(title, error.Message);
            }
        }
        finally
        {
            _isBusy = false;
            if (!_disposed)
            {
                NotifyState();
            }
        }
    }

    private void AdoptResult(DpViaCorridorAnalysis analysis)
    {
        _analysis = analysis;
        DpViaCorridorResult result = analysis.Result;
        _settingsChanged = false;
        _hasProblem = !result.HasCompleteInputs;
        _statusTitle = !result.HasCompleteInputs
            ? "Review required: incomplete inputs"
            : result.FindingCount == 0
                ? "No crossings reported by screening"
                : "Crossings ready to review";
        string timing = analysis.Timings is { } stages
            ? $" Stage timings: acquisition {stages.AcquisitionMilliseconds} ms; " +
                $"managed analysis {stages.AnalysisMilliseconds} ms; " +
                $"report {stages.ReportMilliseconds} ms." +
                AcquisitionBreakdown(stages) +
                CaptureResourceBreakdown(stages)
            : string.Empty;
        _statusDetail = (!result.HasCompleteInputs
            ? $"{result.CoverageWarnings.Count} Engine-data limitations are " +
                "listed in the report. No clear/pass conclusion is permitted. " +
                result.CoverageWarnings[0]
            : "Managed screening and report are complete. This is not a clearance " +
                "or SI simulation. Select a crossing for fresh Engine navigation " +
                "and WPF capture.") + timing;
        RefreshFindings();
        NotifyState();
        FollowSelectedFinding();
    }

    private static string AcquisitionBreakdown(DpViaCorridorTimings timings)
    {
        var phases = new (string Name, long? Milliseconds)[]
        {
            ("native command", timings.NativeCommandMilliseconds),
            ("snapshot transfer", timings.SnapshotTransferMilliseconds),
            ("native release", timings.NativeReleaseMilliseconds),
            ("snapshot replay + conversion", timings.SnapshotReplayAndConversionMilliseconds),
            ("scene construction", timings.SceneConstructionMilliseconds),
            ("snapshot cleanup", timings.SnapshotDisposalMilliseconds),
        };
        string[] available = phases
            .Where(static phase => phase.Milliseconds is not null)
            .Select(static phase => $"{phase.Name} {phase.Milliseconds} ms")
            .ToArray();
        return available.Length == 0
            ? string.Empty
            : " Acquisition detail: " + string.Join("; ", available) + ".";
    }

    internal static string CaptureResourceBreakdown(DpViaCorridorTimings timings)
    {
        if (timings.NativeResources is not { Count: > 0 } resources)
        {
            return string.Empty;
        }

        var labels = new (string Kind, string Label, bool IncludeLimit)[]
        {
            ("objects", "objects", true),
            ("object_visits", "object visits", true),
            ("pad_queries", "pad queries", true),
            ("pages", "pages", true),
            ("spool_bytes", "spool bytes", true),
            ("trace_objects", "traces", false),
            ("via_objects", "vias", false),
            ("shape_objects", "shapes", false),
            ("discarded_scalar_surface_expansions", "scalar surfaces skipped", false),
        };
        string[] values = labels
            .Select(item => (Descriptor: item, Resource: resources.SingleOrDefault(
                resource => resource.Kind == item.Kind)))
            .Where(static value => value.Resource is not null)
            .Select(static value => value.Descriptor.IncludeLimit
                ? $"{value.Descriptor.Label} {value.Resource!.Observed}/{value.Resource.Limit}"
                : $"{value.Descriptor.Label} {value.Resource!.Observed}")
            .ToArray();
        return values.Length == 0
            ? string.Empty
            : " Capture resources: " + string.Join("; ", values) + ".";
    }

    private void RefreshFindings()
    {
        string? selectedId = _selectedFinding?.Id;
        _visibleFindings.Clear();
        foreach (DpViaCorridorFinding finding in CurrentResult?.Findings ?? [])
        {
            if (RiskFilter != "All risks" && finding.Risk != RiskFilter)
            {
                continue;
            }

            if (SearchText.Length > 0 &&
                !new[]
                {
                    finding.PairName,
                    finding.AggressorNet,
                    finding.Layer,
                    finding.Category,
                }.Any(text => text.Contains(
                    SearchText.Trim(),
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            _visibleFindings.Add(finding);
        }
        SelectedFinding =
            _visibleFindings.FirstOrDefault(item => item.Id == selectedId) ??
            _visibleFindings.FirstOrDefault();
        Changed(nameof(HasVisibleFindings));
        Changed(nameof(FindingListSummary));
        Changed(nameof(EmptyResultsTitle));
        Changed(nameof(EmptyResultsDetail));
    }

    private void Session_StateChanged(
        object? sender,
        EngineSessionSnapshot state)
    {
        Dispatch(() => AdoptSessionState(state));
    }

    private void AdoptSessionState(EngineSessionSnapshot state)
    {
        if (_disposed)
        {
            return;
        }

        bool documentChanged =
            _state.ConnectionState != state.ConnectionState ||
            _state.Document != state.Document;
        _state = state;
        if (documentChanged)
        {
            InvalidateCapturedReview();
        }

        if (!_isBusy && !_hasProblem)
        {
            if (CurrentResult is not null && !IsResultCurrent)
            {
                _statusTitle = "Previous result";
                _statusDetail = ResultScope;
            }
            else if (CurrentResult is null)
            {
                _statusTitle = HasReadySession
                    ? "Ready to check"
                    : "Connect to Allegro";
                _statusDetail = RunAvailability;
            }
        }
        NotifyState();
    }

    private void Presentation_StateChanged(
        object? sender,
        EngineWpfPresentationState state)
    {
        Dispatch(() =>
        {
            if (_disposed)
            {
                return;
            }
            if (!state.IsAvailable)
            {
                if (_capturedReview is not null &&
                    _workspaceVisible &&
                    ShowHighlighting)
                {
                    _boardOverlayError =
                        "Allegro overlay unavailable: " +
                        (state.UnavailableReason ??
                            "Engine/WPF presentation evidence is unavailable.");
                }
            }
            NotifyState();
        });
    }

    private void Presentation_OverlayStateChanged(
        object? sender,
        EngineWpfOverlayState state)
    {
        Dispatch(() =>
        {
            if (_disposed)
            {
                return;
            }
            AdoptOverlayState(state);
            Changed(nameof(BoardOverlayStatus));
            _debugCapture.TryCaptureAuto(
                state,
                _selectedFinding?.Id,
                _verifiedZoom?.ActualBounds.ToString());
        });
    }

    private void AdoptOverlayState(EngineWpfOverlayState state)
    {
        if (!TryGetCurrentOverlayIdentity(
                out WorkspaceDocumentIdentity? document,
                out Guid captureId,
                out long revision) ||
            state.Document != document ||
            state.CaptureId != captureId ||
            state.Revision != revision)
        {
            return;
        }

        _boardOverlayError = state.Availability == EngineWpfOverlayAvailability.Unavailable
            ? "Allegro overlay unavailable: " +
                (state.UnavailableReason ?? "No current overlay pixels were published.")
            : string.Empty;
    }

    private bool TryGetCurrentOverlayIdentity(
        out WorkspaceDocumentIdentity? document,
        out Guid captureId,
        out long revision)
    {
        document = null;
        captureId = Guid.Empty;
        revision = 0;
        if (_capturedReview is not { } capture ||
            _analysis?.LiveScene is not { } source ||
            _verifiedZoom is null ||
            !_workspaceVisible ||
            !ShowHighlighting ||
            !IsResultCurrent)
        {
            return false;
        }

        document = source.Document;
        captureId = source.Scene.Identity.CaptureId;
        revision = capture.DrawingSource.Drawings.Revision;
        return true;
    }

    private void RetainSelectionTiming(DpViaCorridorSelectionTiming timing)
    {
        _recentSelectionTimings.Add(timing);
        while (_recentSelectionTimings.Count > 8)
        {
            _recentSelectionTimings.RemoveAt(0);
        }
        Changed(nameof(SelectedFindingDetail));
    }

    private void StampOverlayTiming(long epoch, long overlayMilliseconds)
    {
        for (int index = _recentSelectionTimings.Count - 1; index >= 0; --index)
        {
            if (_recentSelectionTimings[index].Epoch == epoch)
            {
                _recentSelectionTimings[index] =
                    _recentSelectionTimings[index] with { OverlayMilliseconds = overlayMilliseconds };
                Changed(nameof(SelectedFindingDetail));
                return;
            }
        }
    }

    private void Dispatch(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }
        if (!_dispatcher.HasShutdownStarted &&
            !_dispatcher.HasShutdownFinished)
        {
            _ = _dispatcher.BeginInvoke(
                DispatcherPriority.DataBind,
                action);
        }
    }

    private void OpenReport()
    {
        if (!CanOpenReport)
        {
            return;
        }

        try
        {
            var start = new ProcessStartInfo("notepad.exe")
            {
                UseShellExecute = false,
            };
            start.ArgumentList.Add(_requestReport);
            Process.Start(start);
        }
        catch (Exception error) when (
            error is Win32Exception or InvalidOperationException)
        {
            Fail("Could not open report", error.Message);
        }
    }

    private void Fail(string title, string detail)
    {
        _isBusy = false;
        _hasProblem = true;
        _statusTitle = title;
        _statusDetail = detail;
        NotifyState();
    }

    private void NotifyState()
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(string.Empty));
        _runCommand.RaiseCanExecuteChanged();
        _openReportCommand.RaiseCanExecuteChanged();
        _zoomCommand.RaiseCanExecuteChanged();
    }

    private void Changed(string name) =>
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(name));

    private bool Set<T>(
        ref T field,
        T value,
        [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Changed(name);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.StateChanged -= Session_StateChanged;
        _presentation.StateChanged -= Presentation_StateChanged;
        _presentation.OverlayStateChanged -= Presentation_OverlayStateChanged;
        _lifetime.Cancel();
        CancelOverlayUpdate();
        _pipeline.CancelCurrent();
        _boardOverlay.Dispose();
        _capturedReview = null;
        _verifiedZoom = null;
        _lifetime.Dispose();
    }
}
