using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Drawing;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Wpf;
using CircuitHub.AllegroBridge.Wpf.Engine;
using PD.PcbTools;
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
    private readonly RelayCommand _cancelCommand;
    private readonly RelayCommand _openReportCommand;
    private readonly RelayCommand _zoomCommand;
    private readonly RelayCommand _revalidateCommand;
    private readonly RelayCommand _zoomCandidatePositiveCommand;
    private readonly RelayCommand _zoomCandidateNegativeCommand;
    private readonly ObservableCollection<DpViaCorridorFinding> _visibleFindings = [];
    private readonly ObservableCollection<DpViaCorridorCandidatePair> _candidatePairs = [];
    private readonly System.Collections.Generic.List<DpViaCorridorFinding> _filteredFindings = new();
    private readonly ObservableCollection<string> _layerOptions = [DpViaCorridorFindingExplorer.AllLayers];
    private readonly RelayCommand _nextPageCommand;
    private readonly RelayCommand _previousPageCommand;
    private const int FindingPageSize = 7;
    private int _currentPage = 1;
    private string _layerFilter = DpViaCorridorFindingExplorer.AllLayers;
    private bool _pagingSelection;
    private bool _crossingsExpanded = true;
    private bool _canvasLabels = true;

    public sealed record CorridorPageEntry(int Number, bool IsCurrent, ICommand GoTo);

    public sealed record CorridorLayerTile(
        string Layer,
        int Crossings,
        bool IsSelected,
        System.Collections.Generic.IReadOnlyList<DpViaCorridorFinding> Findings,
        ICommand Select);
    private EngineSessionSnapshot _state;
    private CorridorDetailedEvidence? _detailedEvidence;
    private CancellationTokenSource? _overlayUpdate;
    private readonly DpViaCorridorSelectionPipeline _pipeline = new();
    private long _lastFollowTriggerTicks;
    private readonly System.Collections.Generic.List<DpViaCorridorSelectionTiming> _recentSelectionTimings = new();
    private string _marginMils = "0";
    private string _moduleFilter = string.Empty;
    private bool _includeUnused;
    private string _searchText = string.Empty;
    private string _riskFilter = DpViaCorridorFindingExplorer.AllRisks;
    private string _statusTitle = "Connect to Allegro";
    private string _statusDetail =
        "Open PD from Allegro with a board loaded to run this check.";
    private bool _isBusy;
    private bool _runCancelled;
    private CancellationTokenSource? _runCancellation;
    private bool _settingsChanged;
    private bool _hasProblem;
    private int _supersededOutcomeCount;
    private string _requestReport = string.Empty;
    private DpViaCorridorAnalysis? _analysis;
    private DpViaCorridorFinding? _selectedFinding;
    private DpViaCorridorCandidateSnapshot? _candidateSnapshot;
    private DpViaCorridorCandidatePair? _selectedCandidate;
    private bool _candidateHistorical;
    private bool _isCandidateNavigating;
    private string _candidateStatus = string.Empty;
    private string _candidateError = string.Empty;
    private CancellationTokenSource? _candidateNavigationCancellation;
    private Guid _candidateOperationId;
    private Guid _candidateRunId;
    private bool _followSelection = true;
    private bool _isNavigating;
    private bool _disposed;
    private string _navigationError = string.Empty;
    private string _navigationPhase = string.Empty;
    private DpViaCorridorReviewCapture? _capturedReview;
    private DpViaCorridorZoomResult? _verifiedZoom;
    private DpViaCorridorNavigationMode _lastNavigationMode;
    private bool _showHighlighting = true;
    private bool _autoCaptureDebugImages;
    private bool _workspaceVisible;
    private string _boardOverlayError = string.Empty;
    private readonly DpViaCorridorPublicationTracker _publication = new();
    private DpViaCorridorZoomResult? _drawingZoom;
    private DpViaCorridorDrawingSource? _drawingSource;
    private readonly System.Collections.Generic.List<(long Epoch, long Milliseconds)> _earlyOverlayMilliseconds = new();

    private DpViaCorridorResult? CurrentResult => _analysis?.Result;

    private sealed record NavigationContext(
        DpViaCorridorNavigationMode Mode,
        long Epoch,
        Guid OperationId,
        string Origin,
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
        string Origin,
        DpViaCorridorNavigationMode ExecutedMode,
        long? OverlayMilliseconds = null);

    public DpViaCorridorWorkspaceViewModel(
        AllegroEngineSession session,
        EngineWpfPresentation presentation,
        Func<Window?>? debugWindowProvider = null,
        Func<string?>? nativeProductProvider = null)
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

        _corridor = new EngineDpViaCorridorService(
            _session,
            nativeProductProvider: nativeProductProvider);
        _boardOverlay = new BoardOverlayController(_session, _presentation);
        _debugCapture = new OverlayDebugCapture(debugWindowProvider);
        _dispatcher = Dispatcher.CurrentDispatcher;
        _state = _session.State;
        _runCommand = new RelayCommand(Run, () => CanRun);
        _cancelCommand = new RelayCommand(CancelRun, () => !_disposed && _isBusy);
        _openReportCommand = new RelayCommand(OpenReport, () => CanOpenReport);
        _zoomCommand = new RelayCommand(() => ZoomSelectedFinding(DpViaCorridorNavigationMode.Browse, origin: DpViaCorridorNavigationOrigin.Explicit), () => CanZoom);
        _revalidateCommand = new RelayCommand(() => ZoomSelectedFinding(DpViaCorridorNavigationMode.Revalidate, origin: DpViaCorridorNavigationOrigin.Explicit), () => CanZoom);
        _zoomCandidatePositiveCommand = new RelayCommand(() => ZoomSelectedCandidate(true), () => CanZoomCandidate);
        _zoomCandidateNegativeCommand = new RelayCommand(() => ZoomSelectedCandidate(false), () => CanZoomCandidate);
        ClearFiltersCommand = new RelayCommand(() =>
        {
            SearchText = string.Empty;
            RiskFilter = DpViaCorridorFindingExplorer.AllRisks;
            LayerFilter = DpViaCorridorFindingExplorer.AllLayers;
        });
        _nextPageCommand = new RelayCommand(() => ShowPage(_currentPage + 1), () => _currentPage < PageCount);
        _previousPageCommand = new RelayCommand(() => ShowPage(_currentPage - 1), () => _currentPage > 1);
        VisibleFindings =
            new ReadOnlyObservableCollection<DpViaCorridorFinding>(_visibleFindings);
        VisibleCandidates =
            new ReadOnlyObservableCollection<DpViaCorridorCandidatePair>(_candidatePairs);
        _session.StateChanged += Session_StateChanged;
        _presentation.StateChanged += Presentation_StateChanged;
        _presentation.OverlayStateChanged += Presentation_OverlayStateChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand RunCommand => _runCommand;

    public ICommand CancelCommand => _cancelCommand;

    public ICommand OpenReportCommand => _openReportCommand;

    public ICommand ZoomCommand => _zoomCommand;
    public ICommand RevalidateCommand => _revalidateCommand;
    public ICommand ZoomCandidatePositiveCommand => _zoomCandidatePositiveCommand;
    public ICommand ZoomCandidateNegativeCommand => _zoomCandidateNegativeCommand;

    public ICommand ClearFiltersCommand { get; }

    public ICommand NextPageCommand => _nextPageCommand;

    public ICommand PreviousPageCommand => _previousPageCommand;

    public IReadOnlyList<string> RiskOptions { get; } =
        [DpViaCorridorFindingExplorer.AllRisks, "CRITICAL", "MEDIUM", "LOW"];

    public ReadOnlyObservableCollection<DpViaCorridorFinding> VisibleFindings { get; }

    public ReadOnlyObservableCollection<DpViaCorridorCandidatePair> VisibleCandidates { get; }

    public bool HasCandidates => _candidatePairs.Count > 0;

    public bool HasCandidatePanel => _candidateSnapshot is not null || HasCandidateError;

    public bool HasSelectedCandidate => _selectedCandidate is not null;

    public bool IsCandidateNavigating => _isCandidateNavigating;

    public string CandidateListTitle => HasCandidateError && !HasCandidates
        ? "Candidate net pairs unavailable"
        : _candidateHistorical
            ? "Candidate pairs — historical (full analysis did not complete)"
            : "Candidate pairs — corridor analysis pending";

    public string CandidateAuthority => _candidateHistorical
        ? "Historical net metadata only. No complete corridor finding or Clear/Pass result is available."
        : "Net navigation only. Full corridor findings are still pending.";

    public string CandidateStatus
    {
        get
        {
            if (_candidateError.Length > 0)
            {
                return _candidateError;
            }
            if (_isCandidateNavigating)
            {
                return _candidateStatus.Length > 0
                    ? _candidateStatus
                    : "Zooming to the selected candidate net in Allegro…";
            }
            if (_candidateStatus.Length > 0)
            {
                return _candidateStatus;
            }
            if (!HasCandidates)
            {
                return "Candidate net pairs appear here before the full capture starts.";
            }
            return $"{_candidatePairs.Count:N0} candidate net pairs from live metadata. " +
                "Select a candidate pair, then zoom to its P or N net. " +
                "Net navigation only; not a via pair, corridor, finding, Clear/Pass, or proof of current copper.";
        }
    }

    public string CandidateError => _candidateError;

    public bool HasCandidateError => _candidateError.Length > 0;

    public string CandidateDetail => _selectedCandidate is null
        ? "Select a candidate pair to zoom to its P or N member net."
        : $"{_selectedCandidate.PairName} · P: {_selectedCandidate.PositiveNet} · N: {_selectedCandidate.NegativeNet}";

    public bool CanZoomCandidate =>
        !_disposed &&
        !_isCandidateNavigating &&
        HasReadySession &&
        HasCapability(EngineCapabilities.Display) &&
        _candidateSnapshot is not null &&
        _candidateSnapshot.LiveScene.IsCurrent &&
        _candidateSnapshot.IsCurrentFor(_state.Document) &&
        _session.Workspace.Document == _candidateSnapshot.Document &&
        _selectedCandidate is not null;

    public string CandidateZoomAvailability
    {
        get
        {
            if (_disposed)
            {
                return "Candidate zoom unavailable: this corridor workspace is closed.";
            }
            if (_isCandidateNavigating)
            {
                return "Candidate net navigation is pending in Allegro…";
            }
            if (!HasReadySession)
            {
                return "Candidate zoom unavailable: connect to one current Allegro board.";
            }
            if (!HasCapability(EngineCapabilities.Display))
            {
                return "Candidate zoom unavailable: " +
                    (CapabilityUnavailableReason(EngineCapabilities.Display) ??
                        "Engine did not provide native display authority.");
            }
            if (_candidateSnapshot is null || !HasCandidates)
            {
                return "Run the check to list candidate net pairs.";
            }
            if (!_candidateSnapshot.LiveScene.IsCurrent ||
                !_candidateSnapshot.IsCurrentFor(_state.Document) ||
                _session.Workspace.Document != _candidateSnapshot.Document)
            {
                return "The candidate Engine scene is no longer current. Run again for fresh candidates.";
            }
            if (_selectedCandidate is null)
            {
                return "Select a candidate pair to zoom to its P or N net.";
            }
            return "Zoom to the selected candidate member net in Allegro. Net navigation only.";
        }
    }

    public int CurrentPage => _currentPage;

    public int PageCount => DpViaCorridorFindingExplorer.PageCount(_filteredFindings.Count, FindingPageSize);

    public string PageDisplay =>
        _filteredFindings.Count == 0
            ? "0 of 0"
            : $"{(_currentPage - 1) * FindingPageSize + 1}–{Math.Min(_currentPage * FindingPageSize, _filteredFindings.Count)} of {_filteredFindings.Count}";

    public IReadOnlyList<CorridorPageEntry> PageEntries
    {
        get
        {
            var entries = new System.Collections.Generic.List<CorridorPageEntry>(PageCount);
            for (int number = 1; number <= PageCount; number++)
            {
                int captured = number;
                entries.Add(new(captured, captured == _currentPage, new RelayCommand(() => ShowPage(captured))));
            }

            return entries;
        }
    }

    public IReadOnlyList<string> LayerOptions => _layerOptions;

    public IReadOnlyList<CorridorLayerTile> LayerTiles =>
        CurrentResult is null
            ? []
            : CurrentResult.Findings
                .GroupBy(finding => finding.Layer, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new CorridorLayerTile(
                    group.Key,
                    group.Count(),
                    group.Key == _layerFilter,
                    group.ToArray(),
                    new RelayCommand(() => LayerFilter = group.Key == _layerFilter ? DpViaCorridorFindingExplorer.AllLayers : group.Key)))
                .ToArray();

    public int CriticalCount => CurrentResult?.Findings.Count(finding => finding.Risk == "CRITICAL") ?? 0;

    public int MediumCount => CurrentResult?.Findings.Count(finding => finding.Risk == "MEDIUM") ?? 0;

    public int LowCount => CurrentResult?.Findings.Count(finding => finding.Risk == "LOW") ?? 0;

    public string BoardUnitsDisplay => CurrentResult?.Units == "millimeters" ? "mm" : "mil";

    public string BoardLayerCountDisplay =>
        CurrentResult is null
            ? "—"
            : CurrentResult.Findings.Select(finding => finding.Layer).Distinct(StringComparer.Ordinal).Count().ToString("N0");

    public bool CrossingsExpanded
    {
        get => _crossingsExpanded;
        set => Set(ref _crossingsExpanded, value);
    }

    public bool CanvasLabels
    {
        get => _canvasLabels;
        set => Set(ref _canvasLabels, value);
    }

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

    public string LayerFilter
    {
        get => _layerFilter;
        set
        {
            if (_layerOptions.Contains(value) && Set(ref _layerFilter, value))
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
            if (value is null && _pagingSelection)
            {
                return;
            }

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

    public DpViaCorridorCandidatePair? SelectedCandidate
    {
        get => _selectedCandidate;
        set
        {
            if (value is not null && !_candidatePairs.Contains(value))
            {
                return;
            }

            if (Set(ref _selectedCandidate, value))
            {
                CancelCandidateNavigation();
                _candidateError = string.Empty;
                _candidateStatus = _candidateSnapshot?.CoverageWarnings.Count is > 0
                    ? $"{_candidateSnapshot.CoverageWarnings.Count} candidate metadata limitations require review."
                    : string.Empty;
                Changed(nameof(CandidateDetail));
                NotifyState();
            }
        }
    }

    public DpViaCorridorResult? Result => CurrentResult;

    public bool HasResult => CurrentResult is not null;

    public bool HasSelectedFinding => _selectedFinding is not null;

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

    internal Func<DpViaCorridorAnalysis, DpViaCorridorFinding, DpViaCorridorNavigationRequest, CancellationToken, Task<DpViaCorridorNavigationOutcome>>? NavigateOverride { get; set; }

    internal Func<DpViaCorridorCandidateSnapshot, DpViaCorridorCandidatePair, bool, DpViaCorridorCandidateNavigationRequest, CancellationToken, Task<DpViaCorridorCandidateNavigationOutcome>>? CandidateNavigateOverride { get; set; }

    internal Func<LiveDesignScene, DrawingScene, CancellationToken, Task<AllegroReviewFrame>>? CaptureReviewOverride { get; set; }

    internal int FollowAttemptCountForTest { get; set; }

    internal int SupersededOutcomeCountForTest => _supersededOutcomeCount;

    internal long DrawingPublishedEpochForTest => _publication.PublishedEpoch;

    internal EngineWpfPublicationReceipt? DrawingReceiptForTest => _publication.PublishedReceipt;

    internal void AdoptResultForTest(DpViaCorridorAnalysis analysis) =>
        AdoptResult(analysis ?? throw new ArgumentNullException(nameof(analysis)));

    internal void AdoptCandidatesForTest(DpViaCorridorCandidateSnapshot snapshot) =>
        AdoptCandidates(snapshot ?? throw new ArgumentNullException(nameof(snapshot)), historical: false);

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

    /// <summary>
    /// Automatic overlay debug capture (window PNG, hash, JSON receipt,
    /// pruning) on overlay-state changes. Off by default: enabling it adds
    /// synchronous UI-thread file I/O to every overlay transition, which
    /// navigation timing campaigns should measure separately.
    /// </summary>
    public bool AutoCaptureDebugImages
    {
        get => _autoCaptureDebugImages;
        set => Set(ref _autoCaptureDebugImages, value);
    }

    public string NavigationModeDisplay => _verifiedZoom is null
        ? "Not navigated"
        : _lastNavigationMode == DpViaCorridorNavigationMode.Browse
            ? "Browsed · witness identity at navigation; crossing analysis remains from the original scan"
            : "Revalidated · selected objects rechecked against a fresh region; crossing analysis remains from the original scan";

    public string DetailedEvidenceDisplay => _detailedEvidence is not { } evidence
        ? "Revalidate a selected crossing to inspect current contour evidence."
        : $"Fresh observation {evidence.NativeOperationId:N}; " +
            $"capture {evidence.FreshScene.Identity.CaptureId:N}; " +
            $"bounded region {evidence.FreshScene.Document.Bounds.Minimum} to " +
            $"{evidence.FreshScene.Document.Bounds.Maximum}; " +
            $"witness verification: {evidence.WitnessVerificationScope}. " +
            (evidence.AggressorKind == CopperKind.Shape
                ? $"Shape contours: {evidence.Status}; resolved regions: {evidence.ResolvedRegionCount}; " +
                    $"holes: {evidence.HoleCount}. "
                : "Shape contour and hole counts do not apply to this conductor. ") +
            string.Join(" ", evidence.Diagnostics);

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
                return "Allegro navigation and review capture are already running.";
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
                ? _lastNavigationMode == DpViaCorridorNavigationMode.Browse
                    ? "Zooming through Engine, then capturing the current Allegro review…"
                    : _navigationPhase.Length > 0
                        ? _navigationPhase
                        : "Revalidating through Engine and capturing the current WPF review…"
                : _capturedReview is { } capture
                    ? $"Captured {capture.CapturedAt.LocalDateTime:HH:mm:ss} · " +
                        $"selected finding layer: {capture.Layer}. The canonical " +
                        "drawing marks the measured corridor, via centers, and " +
                        "intrusion—not copper outlines. Wheel to magnify; Fit to reset."
                    : ZoomAvailability;

    public bool HasProblem =>
        !_isBusy && !_runCancelled &&
        (_hasProblem || CurrentResult is { HasCompleteInputs: false });

    public bool IsResultCurrent =>
        CurrentResult is not null &&
        !_settingsChanged &&
        _state.ConnectionState == EngineConnectionState.Ready &&
        _state.Document is { } document &&
        CurrentResult.BoardGeneration == document.BoardGeneration &&
        _analysis!.IsCurrentFor(document) &&
        (_analysis.ScalableScan is not null || _analysis.LiveScene?.IsCurrent == true);

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
            ? Path.GetFileName(_state.Document?.Design) ?? "Current PCB"
            : "No live board";

    public string BoardPathDisplay =>
        HasReadySession
            ? _state.Document?.Design ?? "Current PCB"
            : "No live board";

    public string StatusTitle => _statusTitle;

    public string StatusDetail => _statusDetail;

    private static bool HasBlockingCoverageGaps(DpViaCorridorResult result) =>
        (result.BlockingCoverageWarnings ?? result.CoverageWarnings).Count > 0;

    public DpViaCorridorRunStatusKind RunStatusKind =>
        DpViaCorridorRunStatus.Classify(
            _isBusy,
            _runCancelled,
            _hasProblem,
            CurrentResult is not null,
            CurrentResult?.HasCompleteInputs ?? false,
            CurrentResult?.CoverageWarnings.Count > 0);

    public string RunStatusText =>
        DpViaCorridorRunStatus.CompactText(
            RunStatusKind,
            _statusTitle,
            CurrentResult?.FindingCount ?? 0,
            HasReadySession,
            IsResultCurrent,
            CurrentResult?.CoverageWarnings.Count ?? 0,
            CurrentResult is null || HasBlockingCoverageGaps(CurrentResult));

    public string RunLabel =>
        _isBusy
            ? "Checking…"
            : HasResult
                ? "Run again"
                : "Run check";

    public string RunAvailability =>
        InputError.Length > 0
            ? InputError
            : _isNavigating
                ? "Wait for Engine navigation and review capture to finish."
                : _isBusy
                    ? "Keep the board open. Cancel is available next to Run."
                    : !HasReadySession
                        ? "A connected Allegro Engine board is required."
                        : CapabilityUnavailableReason(EngineCapabilities.SceneRead) ??
                            "Captures the board through Engine and screens bounded corridor areas.";

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
                $"source {CurrentResult.SourceUnits} · display mils" +
                (CurrentResult.SourceExportedAt is { } exportedAt
                    ? $" · snapshot exported {exportedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}"
                    : CurrentResult.CaptureStartedAt is { } captureStart &&
                      CurrentResult.CaptureCompletedAt is { } captureEnd
                        ? $" · acquired {captureStart.ToLocalTime():yyyy-MM-dd HH:mm:ss}–" +
                          $"{captureEnd.ToLocalTime():HH:mm:ss}"
                        : " · capture time unavailable");

    public string ResultScope
    {
        get
        {
            if (CurrentResult is not { } current)
            {
                return "Results will appear here after analysis.";
            }
            if (_settingsChanged)
            {
                return "Settings changed. Run again to analyze with these options.";
            }
            if (!IsResultCurrent)
            {
                return "The Engine document changed. These results are historical; run again.";
            }
            return CurrentResultScopeText(current);
        }
    }

    public string FindingListSummary =>
        CurrentResult is null
            ? "No analysis yet"
            : $"{_visibleFindings.Count:N0} shown · " +
                $"{CurrentResult.FindingCount:N0} total";

    public string EmptyResultsTitle
    {
        get
        {
            if (CurrentResult is not { } current)
            {
                return "Run a check to review crossings";
            }
            if (HasBlockingCoverageGaps(current))
            {
                return "Review required: incomplete inputs";
            }
            if (current.CoverageWarnings.Count > 0)
            {
                return AdvisoryEmptyResultsTitle(current.CoverageWarnings.Count);
            }
            return current.FindingCount == 0
                ? "No crossings in captured screening"
                : "No matching crossings";
        }
    }

    public string EmptyResultsDetail
    {
        get
        {
            if (CurrentResult is not { } current)
            {
                return "The checker will return the affected pair, aggressor, layer, " +
                    "and captured geometry.";
            }
            if (HasBlockingCoverageGaps(current))
            {
                return "Required Engine data was unavailable. The absence of displayed " +
                    "crossings is not a pass; read the coverage warnings in the report.";
            }
            if (current.CoverageWarnings.Count > 0)
            {
                return AdvisoryEmptyResultsDetail(current.CoverageWarnings.Count);
            }
            return current.FindingCount == 0
                ? "This run found no corridor crossings in its analyzed scope. " +
                    "Live Clear/Pass is withheld until current state is verified."
                : "Change the search or risk filter to see other captured crossings.";
        }
    }

    /// <summary>
    /// Scope line for an adopted current result. Blocking gaps keep the
    /// incomplete-inputs wording; advisory-only warnings name their count
    /// and note the gaps cannot hide a crossing while still requiring
    /// review and withholding any clear result.
    /// </summary>
    internal static string CurrentResultScopeText(DpViaCorridorResult result)
    {
        if (HasBlockingCoverageGaps(result))
        {
            return "Incomplete Engine inputs. Findings are review information only; " +
                "a clear result cannot be established. See the report.";
        }
        if (result.CoverageWarnings.Count > 0)
        {
            return AdvisoryResultScopeText(result.CoverageWarnings.Count);
        }
        return "Captured board state, not a verified live-current clearance. " +
            "Risk classifications are advisory; live Clear/Pass is withheld.";
    }

    private static string AdvisoryResultScopeText(int warningCount) =>
        $"Review required: {warningCount:N0} advisory coverage " +
        (warningCount == 1 ? "warning" : "warnings") +
        ". The " +
        (warningCount == 1 ? "gap" : "gaps") +
        " cannot hide a crossing; findings are review information only and " +
        "a clear result cannot be established. See the report.";

    private static string AdvisoryEmptyResultsTitle(int warningCount) =>
        $"Review required: {warningCount:N0} coverage " +
        (warningCount == 1 ? "warning" : "warnings");

    private static string AdvisoryEmptyResultsDetail(int warningCount) =>
        $"{warningCount:N0} advisory coverage " +
        (warningCount == 1 ? "warning is" : "warnings are") +
        " listed in the report; the " +
        (warningCount == 1 ? "gap" : "gaps") +
        " cannot hide a crossing but review is required. " +
        "The absence of displayed crossings is not a pass; " +
        "read the coverage warnings in the report.";

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
                    : (timing.NavigationPhases.Mode == DpViaCorridorNavigationMode.Browse
                        ? $"browse {timing.NavigationPhases.ZoomMilliseconds} ms (no region read)"
                        : $"region {timing.NavigationPhases.RegionMilliseconds} ms" +
                            RegionInnerSuffix(timing.NavigationPhases) +
                            $"; validate {timing.NavigationPhases.WitnessValidationMilliseconds} ms; " +
                            $"zoom {timing.NavigationPhases.ZoomMilliseconds} ms");
                return $" · selection {timing.Epoch}: navigate {timing.NavigateMilliseconds} ms; " +
                    $"capture {timing.CaptureMilliseconds} ms; overlay {timing.OverlayMilliseconds} ms; " +
                    phases;
            }
        }
        return string.Empty;
    }

    private static string RegionInnerSuffix(DpViaCorridorNavigationPhases phases)
    {
        var parts = new System.Collections.Generic.List<string>(8);
        AddInner(parts, "native", phases.NativeReadMilliseconds);
        AddInner(parts, "decode", phases.RegionDecodingMilliseconds);
        AddInner(parts, "convert", phases.RegionConversionMilliseconds);
        AddInner(parts, "enum", phases.NativeEnumerationMilliseconds);
        AddInner(parts, "meta", phases.NativeMetadataMilliseconds);
        AddInner(parts, "pad", phases.NativePadMilliseconds);
        AddInner(parts, "contour", phases.NativeContourMilliseconds);
        AddInner(parts, "loop", phases.NativeObjectLoopMilliseconds);
        return parts.Count == 0 ? string.Empty : " (" + string.Join("; ", parts) + ")";
    }

    private static void AddInner(System.Collections.Generic.List<string> parts, string name, long? milliseconds)
    {
        if (milliseconds is not null)
        {
            parts.Add($"{name} {milliseconds} ms");
        }
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
        _detailedEvidence = null;
        _publication.Supersede();
        _drawingZoom = null;
        _drawingSource = null;
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
            !IsResultCurrent)
        {
            _boardOverlay.Clear();
            return;
        }

        if (_capturedReview is { } capture &&
            _verifiedZoom is { } zoom &&
            _selectedFinding is { } finding &&
            _analysis?.LiveScene is { } source)
        {
            DrawingScene drawings = capture.DrawingSource.ForLive(source.Scene);
            if (IsDrawingLiveFor(source, drawings.Revision))
            {
                // The drawing-first publication already made this exact
                // revision visible; the landed review changes no pixels.
                AdoptOverlayState(_presentation.OverlayState);
                NotifyState();
                return;
            }
            _ = PublishOverlayCore(
                zoom,
                finding,
                source,
                drawings,
                capture.DrawingSource.Drawings.Revision,
                capture,
                _pipeline.CurrentEpoch);
            return;
        }

        // Independent drawing-first lifecycle: no review, but a completed
        // visible drawing publication retained for the current selection.
        if (_publication.PublishedEpoch == _pipeline.CurrentEpoch &&
            _drawingZoom is { } drawingZoom &&
            _drawingSource is { } drawingSource &&
            _selectedFinding is { } liveFinding &&
            _analysis?.LiveScene is { } liveSource)
        {
            _ = PublishOverlayCore(
                drawingZoom,
                liveFinding,
                liveSource,
                drawingSource.ForLive(liveSource.Scene),
                drawingSource.Drawings.Revision,
                null,
                _publication.PublishedEpoch);
            return;
        }

        _boardOverlay.Clear();
    }

    private bool IsDrawingLiveFor(LiveDesignScene source, long revision) =>
        _presentation.OverlayState.IsVisibleFor(
            source.Document,
            source.Scene.Identity.CaptureId,
            revision);

    /// <summary>
    /// Drawing-first publication: publishes the verified-zoom Engine drawing
    /// immediately, without requiring review pixels, and awaits the actual
    /// completion receipt. Only a visible receipt for the still-current
    /// epoch marks the drawing published and retains it; anything else
    /// leaves the review path to publish when its capture lands.
    /// </summary>
    private async Task PublishVerifiedDrawingAsync(
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
        EngineWpfPublicationReceipt? receipt = await PublishOverlayCore(
            zoom,
            finding,
            source,
            drawingSource.ForLive(source.Scene),
            drawingSource.Drawings.Revision,
            null,
            epoch);
        if (_publication.Complete(epoch, _pipeline.CurrentEpoch, _presentation.CurrentSequence, receipt))
        {
            _drawingZoom = zoom;
            _drawingSource = drawingSource;
        }
    }

    private Task<EngineWpfPublicationReceipt?> PublishOverlayCore(
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
        return PresentBoardOverlayAsync(
            overlay,
            capture,
            epoch,
            cancellation);
    }

    private async Task<EngineWpfPublicationReceipt?> PresentBoardOverlayAsync(
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
            return receipt;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return null;
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
            return null;
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
            ZoomSelectedFinding(DpViaCorridorNavigationMode.Browse, awaitQuietPeriod: true, origin: DpViaCorridorNavigationOrigin.Follow);
        }
    }

    private async void ZoomSelectedFinding(
        DpViaCorridorNavigationMode mode = DpViaCorridorNavigationMode.Browse,
        bool awaitQuietPeriod = false,
        string origin = DpViaCorridorNavigationOrigin.Follow)
    {
        if (!CanZoom ||
            _analysis is not { } analysis ||
            _selectedFinding is not { } finding)
        {
            if (string.Equals(origin, DpViaCorridorNavigationOrigin.Explicit, StringComparison.Ordinal))
            {
                _navigationError = $"Navigation request ({mode}) not started: {ZoomAvailability}";
                NotifyState();
            }
            return;
        }

        LiveDesignScene? source = analysis.LiveScene;
        SupersedeSelection();
        var context = new NavigationContext(
            mode,
            _pipeline.CurrentEpoch,
            Guid.NewGuid(),
            origin,
            analysis.Document,
            analysis.CaptureId ?? throw new InvalidDataException("The corridor analysis has no capture identity."),
            analysis.Result.ReportPath,
            finding.Id,
            finding.Layer);
        _lastNavigationMode = mode;
        _navigationPhase = mode == DpViaCorridorNavigationMode.Revalidate
            ? "Preparing fresh witness revalidation…"
            : string.Empty;
        _isNavigating = true;
        NotifyState();
        Task<T> DispatchWhenNativeIdle<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken token,
            bool marshalToDispatcher) =>
            EngineNativeRetry.ExecuteWhenIdleAsync(
                operation,
                () => IsNavigationCurrent(context),
                dispatch: marshalToDispatcher
                    ? (attempt, attemptToken) => _presentation.InvokeOnDispatcherAsync(
                        () => attempt(attemptToken))
                    : null,
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
        DpViaCorridorNavigationRequest request = new(context.OperationId, context.Origin)
        {
            Progress = mode == DpViaCorridorNavigationMode.Revalidate
                ? new Progress<DpViaCorridorNavigationStage>(stage =>
                {
                    if (!_isNavigating || !IsNavigationCurrent(context))
                    {
                        return;
                    }
                    _navigationPhase = stage switch
                    {
                        DpViaCorridorNavigationStage.ReadingRegion =>
                            "Revalidating: reading the fresh native region…",
                        DpViaCorridorNavigationStage.MatchingWitnesses =>
                            "Revalidating: matching selected witnesses…",
                        DpViaCorridorNavigationStage.Zooming =>
                            "Revalidating: zooming to verified witnesses…",
                        DpViaCorridorNavigationStage.ReadingPresentation =>
                            "Revalidating: reading the live presentation…",
                        _ => "Revalidating the selected crossing…",
                    };
                    NotifyState();
                })
                : null,
        };
        LiveDesignScene RequirePresentationSource() => source ??
            throw new InvalidDataException("The selected finding has no verified live presentation scene.");
        var operations = new DpViaCorridorSelectionOperations(
            NavigateAsync: async (selection, token) =>
            {
                DpViaCorridorNavigationOutcome outcome;
                if (NavigateOverride is not null)
                {
                    outcome = await NavigateOverride(analysis, finding, request, token);
                }
                else
                {
                    outcome = await DispatchWhenNativeIdle(
                        dispatch => mode == DpViaCorridorNavigationMode.Browse
                            ? _corridor.BrowseAsync(analysis, finding, request, dispatch)
                            : _corridor.NavigateAsync(analysis, finding, request, dispatch),
                        token,
                        marshalToDispatcher: false);
                }
                source = analysis.LiveScene ?? throw new InvalidDataException(
                    "The verified navigation did not provide a live presentation scene.");
                return outcome;
            },
            PublishDrawingAsync: async (selection, navigation, navigateMilliseconds, token) =>
            {
                await PublishVerifiedDrawingAsync(
                    navigation.Zoom, finding, RequirePresentationSource(), selection.Epoch);
            },
            CaptureAsync: async (selection, token) =>
            {
                LiveDesignScene liveSource = RequirePresentationSource();
                DpViaCorridorDrawingSource drawingSource =
                    BoardOverlayDrawingPolicy.CorridorSource(
                        liveSource.Scene,
                        finding,
                        selection.Epoch);
                DrawingScene drawings = drawingSource.ForReview(liveSource.Scene);
                if (CaptureReviewOverride is not null)
                {
                    return await CaptureReviewOverride(liveSource, drawings, token);
                }
                return await DispatchWhenNativeIdle(
                    async dispatch => await _presentation.CaptureReviewAsync(
                        liveSource,
                        drawings,
                        dispatch),
                    token,
                    marshalToDispatcher: true);
            },
            PublishAsync: (selection, navigation, review, navigateMilliseconds, captureMilliseconds, token) =>
            {
                if (DpViaCorridorOutcomeAttribution.IsForeignOutcome(
                    navigation,
                    context.OperationId,
                    finding.Id))
                {
                    _supersededOutcomeCount++;
                    return Task.CompletedTask;
                }
                LiveDesignScene liveSource = RequirePresentationSource();
                DpViaCorridorReviewCapture capture =
                    DpViaCorridorReviewCapture.Create(
                        review,
                        liveSource,
                        BoardOverlayDrawingPolicy.CorridorSource(
                            liveSource.Scene,
                            finding,
                            selection.Epoch),
                        navigation.Zoom);
                _verifiedZoom = navigation.Zoom;
                _detailedEvidence = navigation.DetailedEvidence;
                _capturedReview = capture;
                _lastNavigationMode = navigation.ExecutedMode;
                _navigationError = string.Empty;
                RetainSelectionTiming(new DpViaCorridorSelectionTiming(
                    selection.Epoch,
                    finding.Id,
                    navigateMilliseconds,
                    captureMilliseconds,
                    navigation.Phases,
                    navigation.Origin,
                    navigation.ExecutedMode));
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
                if (_publication.PublishedEpoch != context.Epoch)
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
            _navigationPhase = string.Empty;
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
        _analysis?.CaptureId == context.CaptureId &&
        CurrentResult?.ReportPath == context.Report &&
        _selectedFinding?.Id == context.FindingId &&
        _selectedFinding.Layer == context.Layer;

    private void AdoptCandidates(DpViaCorridorCandidateSnapshot snapshot, bool historical)
    {
        if (_disposed)
        {
            return;
        }
        if (!snapshot.IsCurrentFor(_state.Document) ||
            _session.Workspace.Document != snapshot.Document)
        {
            _candidateError = "Candidate metadata was discarded because the Engine board identity changed.";
            NotifyState();
            return;
        }

        CancelCandidateNavigation();
        _candidateSnapshot = snapshot;
        _candidateHistorical = historical;
        _candidateError = string.Empty;
        _candidateStatus = snapshot.CoverageWarnings.Count == 0
            ? string.Empty
            : $"{snapshot.CoverageWarnings.Count} candidate metadata limitations require review.";
        _selectedCandidate = null;
        _candidatePairs.Clear();
        foreach (DpViaCorridorCandidatePair pair in snapshot.Pairs)
        {
            _candidatePairs.Add(pair);
        }
        _selectedCandidate = _candidatePairs.FirstOrDefault();
        NotifyState();
    }

    private void ClearCandidates()
    {
        _candidateRunId = Guid.Empty;
        CancelCandidateNavigation();
        _candidateSnapshot = null;
        _selectedCandidate = null;
        _candidateHistorical = false;
        _candidateError = string.Empty;
        _candidateStatus = string.Empty;
        _candidatePairs.Clear();
        NotifyState();
    }

    private void RetainHistoricalCandidates()
    {
        if (_candidateSnapshot is null)
        {
            return;
        }
        _candidateHistorical = true;
        _candidateStatus = "Full corridor analysis did not complete. Candidate net names are historical metadata only.";
        NotifyState();
    }

    private void CancelCandidateNavigation()
    {
        _candidateOperationId = Guid.NewGuid();
        _candidateNavigationCancellation?.Cancel();
        _candidateNavigationCancellation?.Dispose();
        _candidateNavigationCancellation = null;
        _isCandidateNavigating = false;
    }

    private bool IsCandidateNavigationCurrent(
        Guid operationId,
        DpViaCorridorCandidateSnapshot snapshot,
        DpViaCorridorCandidatePair pair) =>
        !_disposed &&
        _candidateOperationId == operationId &&
        ReferenceEquals(_candidateSnapshot, snapshot) &&
        Equals(_selectedCandidate, pair) &&
        snapshot.IsCurrentFor(_state.Document) &&
        _session.Workspace.Document == snapshot.Document;

    private async void ZoomSelectedCandidate(bool positive)
    {
        if (!CanZoomCandidate ||
            _candidateSnapshot is not { } snapshot ||
            _selectedCandidate is not { } pair)
        {
            return;
        }

        var request = new DpViaCorridorCandidateNavigationRequest(
            Guid.NewGuid(), "candidate-list");
        _candidateOperationId = request.OperationId;
        CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token);
        _candidateNavigationCancellation = cancellation;
        _isCandidateNavigating = true;
        _candidateError = string.Empty;
        _candidateStatus = "Waiting for Allegro to zoom the selected candidate net…";
        NotifyState();
        try
        {
            DpViaCorridorCandidateNavigationOutcome outcome = CandidateNavigateOverride is { } navigate
                ? await navigate(snapshot, pair, positive, request, cancellation.Token)
                : await _corridor.ZoomCandidateNetAsync(
                    snapshot, pair, positive, request, cancellation.Token);
            if (IsCandidateNavigationCurrent(request.OperationId, snapshot, pair) &&
                outcome.OperationId == request.OperationId &&
                outcome.CaptureId == snapshot.CaptureId &&
                outcome.PairName == pair.PairName &&
                outcome.NetName == (positive ? pair.PositiveNet : pair.NegativeNet))
            {
                _candidateStatus = $"Showing candidate net {outcome.NetName} in Allegro " +
                    $"({outcome.ZoomMilliseconds:N0} ms). This is net navigation only.";
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (IsCandidateNavigationCurrent(request.OperationId, snapshot, pair))
            {
                _candidateError = "Candidate net zoom unavailable: " + error.Message;
            }
        }
        finally
        {
            if (_candidateOperationId == request.OperationId)
            {
                _candidateNavigationCancellation = null;
                _isCandidateNavigating = false;
                if (!_disposed)
                {
                    NotifyState();
                }
            }
            cancellation.Dispose();
        }
    }

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
            ClearCandidates();
            Guid candidateRunId = Guid.NewGuid();
            _candidateRunId = candidateRunId;
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
            _runCancelled = false;
            _hasProblem = false;
            _settingsChanged = true;
            _statusTitle = "Checking in Allegro";
            _statusDetail =
                "Engine is capturing the board; corridor areas are screened from the sealed capture.";
            _runCancellation?.Dispose();
            _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            NotifyState();
            dispatched = true;
            DpViaCorridorAnalysis analysis = await _corridor.AnalyzeAsync(
                new DpViaCorridorOptions(
                    margin,
                    ModuleFilter.Trim(),
                    IncludeUnused)
                {
                    Progress = new Progress<string>(message =>
                    {
                        if (_isBusy && !_disposed && _candidateRunId == candidateRunId)
                        {
                            _statusDetail = message;
                            NotifyState();
                        }
                    }),
                    CandidateProgress = new Progress<DpViaCorridorCandidateSnapshot>(snapshot =>
                    {
                        if (_isBusy && !_disposed && _candidateRunId == candidateRunId)
                        {
                            AdoptCandidates(snapshot, historical: false);
                        }
                    }),
                    CandidateIssueProgress = new Progress<string>(message =>
                    {
                        if (_isBusy && !_disposed && _candidateRunId == candidateRunId)
                        {
                            _candidateError = "Candidate metadata unavailable: " + message;
                            NotifyState();
                        }
                    }),
                },
                _requestReport,
                _runCancellation.Token);
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
        catch (OperationCanceledException)
        {
            if (!_disposed)
            {
                if (_runCancellation?.IsCancellationRequested == true)
                {
                    MarkRunCancelled();
                }
                else
                {
                    Fail(
                        "Check did not complete",
                        "The analysis stopped before Engine results were adopted.");
                }
            }
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
            _runCancellation?.Dispose();
            _runCancellation = null;
            if (!_disposed)
            {
                NotifyState();
            }
        }
    }

    private void AdoptResult(DpViaCorridorAnalysis analysis)
    {
        ClearCandidates();
        _analysis = analysis;
        DpViaCorridorResult result = analysis.Result;
        _settingsChanged = false;
        _runCancelled = false;
        // Incomplete coverage is an adopted result state, not an execution
        // failure. Keep the failure flag reserved for Fail() so the status
        // classifier can distinguish amber review-required from red failure.
        _hasProblem = false;
        int warningCount = result.CoverageWarnings.Count;
        bool blockingGaps = HasBlockingCoverageGaps(result);
        _statusTitle = blockingGaps
            ? "Review required: incomplete inputs"
            : warningCount > 0
                ? $"Review required: {warningCount:N0} coverage " +
                    (warningCount == 1 ? "warning" : "warnings")
                : result.FindingCount == 0
                    ? "Snapshot: no crossings reported"
                    : "Captured crossings ready to review";
        string captureWindow = result.SourceExportedAt is { } exportedAt
            ? $" Snapshot exported {exportedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}."
            : result.CaptureStartedAt is { } captureStart &&
              result.CaptureCompletedAt is { } captureEnd
                ? $" Acquired {captureStart.ToLocalTime():yyyy-MM-dd HH:mm:ss}–" +
                  $"{captureEnd.ToLocalTime():HH:mm:ss}."
                : " Capture time unavailable.";
        string timing = analysis.Timings is { } stages
            ? $" Stage timings: acquisition {stages.AcquisitionMilliseconds} ms; " +
                $"managed analysis {stages.AnalysisMilliseconds} ms; " +
                $"report {stages.ReportMilliseconds} ms." +
                AcquisitionBreakdown(stages) +
                CaptureResourceBreakdown(stages)
            : string.Empty;
        _statusDetail = (blockingGaps
            ? $"{warningCount} Engine-data limitations are " +
                "listed in the report. No clear/pass conclusion is permitted. " +
                result.CoverageWarnings[0]
            : warningCount > 0
                ? $"{warningCount} advisory coverage " +
                    (warningCount == 1 ? "warning is" : "warnings are") +
                    " listed in the report; the gaps cannot hide a crossing " +
                    "but review is required. No clear/pass conclusion is " +
                    "permitted. " +
                    result.CoverageWarnings[0]
                : "Snapshot screening and report are complete. This is not a live " +
                "Clear/Pass or SI simulation. Select a crossing for Engine " +
                "navigation and WPF review.") + captureWindow + timing +
                AcquisitionEvidenceSuffix(analysis);
        System.Collections.Generic.List<string> wanted =
            DpViaCorridorFindingExplorer.DeriveLayers(result.Findings);
        for (int index = _layerOptions.Count - 1; index >= 0; index--)
        {
            if (_layerOptions[index] != DpViaCorridorFindingExplorer.AllLayers && !wanted.Contains(_layerOptions[index]))
            {
                _layerOptions.RemoveAt(index);
            }
        }
        foreach (string layer in wanted)
        {
            if (!_layerOptions.Contains(layer))
            {
                int insert = 1;
                while (insert < _layerOptions.Count &&
                    string.Compare(_layerOptions[insert], layer, StringComparison.Ordinal) < 0)
                {
                    insert++;
                }
                _layerOptions.Insert(insert, layer);
            }
        }
        if (!_layerOptions.Contains(_layerFilter))
        {
            LayerFilter = DpViaCorridorFindingExplorer.AllLayers;
        }
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

    private static string AcquisitionEvidenceSuffix(DpViaCorridorAnalysis analysis)
    {
        if (analysis.CaptureEvidence is not { } evidence)
        {
            return string.Empty;
        }
        var capture = evidence.Capture;
        var counts = evidence.Run.Counts;
        if (evidence.PlanningCapture is { } planning && planning.Identity != capture.Identity)
        {
            return StagedAcquisitionEvidenceSuffix(evidence, planning, capture, counts);
        }
        string resources = string.Join("; ", capture.Resources.Select(resource =>
            $"{resource.Kind}: {resource.Observed}/{resource.Limit}, exhausted={resource.Exhausted}"));
        return $" Sealed capture: {capture.RecordCount:N0} records, {capture.PageCount:N0} pages, " +
            $"{capture.StoredBytes:N0} stored bytes. Global via replay: " +
            $"{counts.GlobalRecordsSelected:N0} selected records, {counts.GlobalPagesRead:N0} pages read. " +
            $"Bounded corridor replay: {counts.CompletedBatches:N0}/{counts.PlannedBatches:N0} areas, " +
            $"{counts.BatchRecordsSelected:N0} selected records, {counts.BatchPagesRead:N0} pages read. " +
            $"Capture resources: {resources}.";
    }

    private static string StagedAcquisitionEvidenceSuffix(
        DpViaCorridorCaptureResult evidence,
        LargeBoards.LargeBoardCaptureStoreInfo planning,
        LargeBoards.LargeBoardCaptureStoreInfo capture,
        LargeBoards.LargeBoardCorridorRunCounts counts)
    {
        // Staged runs seal two distinct stores: the planning catalog (A) and
        // the positional scan (B). Each keeps its own sealed counts and
        // resource use; per-store limits are never summed.
        string planningResources = FormatCaptureResources(planning);
        string scanResources = FormatCaptureResources(capture);
        string startup = evidence.ObservationStartupMilliseconds is { } startupMilliseconds
            ? $" Observation startup charged once: {startupMilliseconds:N0} ms."
            : string.Empty;
        return $" Sealed stage A planning capture: {planning.RecordCount:N0} records, {planning.PageCount:N0} pages, " +
            $"{planning.StoredBytes:N0} stored bytes. Sealed stage B scan capture: " +
            $"{capture.RecordCount:N0} records, {capture.PageCount:N0} pages, " +
            $"{capture.StoredBytes:N0} stored bytes. Planning (A) via replay: " +
            $"{counts.GlobalRecordsSelected:N0} selected records, {counts.GlobalPagesRead:N0} pages read. " +
            $"Bounded corridor replay (B): {counts.CompletedBatches:N0}/{counts.PlannedBatches:N0} areas, " +
            $"{counts.BatchRecordsSelected:N0} selected records, {counts.BatchPagesRead:N0} pages read. " +
            $"Stage A capture resources: {planningResources}. Stage B capture resources: {scanResources}." +
            startup;
    }

    private static string FormatCaptureResources(LargeBoards.LargeBoardCaptureStoreInfo capture) =>
        string.Join("; ", capture.Resources.Select(resource =>
            $"{resource.Kind}: {resource.Observed}/{resource.Limit}, exhausted={resource.Exhausted}"));

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
        _filteredFindings.Clear();
        _filteredFindings.AddRange(DpViaCorridorFindingExplorer.ApplyFilters(
            CurrentResult?.Findings ?? [],
            RiskFilter,
            LayerFilter,
            SearchText));
        _currentPage = 1;
        ShowPage(_currentPage);
        Changed(nameof(EmptyResultsTitle));
        Changed(nameof(EmptyResultsDetail));
    }

    private void ShowPage(int page)
    {
        _currentPage = Math.Clamp(page, 1, PageCount);
        string? selectedId = _selectedFinding?.Id;
        _pagingSelection = true;
        try
        {
            _visibleFindings.Clear();
            foreach (DpViaCorridorFinding finding in DpViaCorridorFindingExplorer.GetPage(
                _filteredFindings, _currentPage, FindingPageSize))
            {
                _visibleFindings.Add(finding);
            }
        }
        finally
        {
            _pagingSelection = false;
        }
        string? resolved = DpViaCorridorFindingExplorer.ResolveSelectedId(
            _filteredFindings, _visibleFindings, selectedId);
        if (resolved is null)
        {
            SelectedFinding = null;
        }
        else if (_visibleFindings.FirstOrDefault(item => item.Id == resolved) is { } same)
        {
            SelectedFinding = same;
        }
        Changed(nameof(CurrentPage));
        Changed(nameof(PageCount));
        Changed(nameof(PageDisplay));
        Changed(nameof(PageEntries));
        Changed(nameof(HasVisibleFindings));
        Changed(nameof(FindingListSummary));
        _nextPageCommand.RaiseCanExecuteChanged();
        _previousPageCommand.RaiseCanExecuteChanged();
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
            CancelCandidateNavigation();
            if (_candidateSnapshot is not null)
            {
                _candidateHistorical = true;
                _candidateStatus = "The Allegro document changed. Candidate metadata is historical; run again to navigate.";
            }
        }

        if (!_isBusy && !_hasProblem &&
            CurrentResult is not { HasCompleteInputs: false })
        {
            if (CurrentResult is not null && !IsResultCurrent)
            {
                _runCancelled = false;
                _statusTitle = "Previous result";
                _statusDetail = ResultScope;
            }
            else if (CurrentResult is null)
            {
                _runCancelled = false;
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
                if ((_capturedReview is not null ||
                        _publication.PublishedEpoch == _pipeline.CurrentEpoch) &&
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
            if (_autoCaptureDebugImages)
            {
                _debugCapture.TryCaptureAuto(
                    state,
                    _selectedFinding?.Id,
                    _verifiedZoom?.ActualBounds.ToString());
            }
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
        if (!_workspaceVisible ||
            !ShowHighlighting ||
            !IsResultCurrent ||
            _analysis?.LiveScene is not { } source)
        {
            return false;
        }

        if (_capturedReview is { } capture && _verifiedZoom is not null)
        {
            document = source.Document;
            captureId = source.Scene.Identity.CaptureId;
            revision = capture.DrawingSource.Drawings.Revision;
            return true;
        }

        if (_drawingSource is { } drawings &&
            _drawingZoom is not null &&
            _publication.PublishedEpoch == _pipeline.CurrentEpoch)
        {
            document = source.Document;
            captureId = source.Scene.Identity.CaptureId;
            revision = drawings.Drawings.Revision;
            return true;
        }

        return false;
    }

    private void RetainSelectionTiming(DpViaCorridorSelectionTiming timing)
    {
        for (int index = 0; index < _earlyOverlayMilliseconds.Count; ++index)
        {
            if (_earlyOverlayMilliseconds[index].Epoch == timing.Epoch)
            {
                timing = timing with { OverlayMilliseconds = _earlyOverlayMilliseconds[index].Milliseconds };
                _earlyOverlayMilliseconds.RemoveAt(index);
                break;
            }
        }
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
        // The drawing-first publication completes before its selection
        // timing record exists; stash the stamp for RetainSelectionTiming.
        _earlyOverlayMilliseconds.Add((epoch, overlayMilliseconds));
        while (_earlyOverlayMilliseconds.Count > 8)
        {
            _earlyOverlayMilliseconds.RemoveAt(0);
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
        _runCancelled = false;
        _hasProblem = true;
        _statusTitle = title;
        _statusDetail = detail;
        RetainHistoricalCandidates();
        NotifyState();
    }

    private void CancelRun()
    {
        _runCancellation?.Cancel();
    }

    private void MarkRunCancelled()
    {
        _isBusy = false;
        _runCancelled = true;
        _hasProblem = false;
        _statusTitle = "Check cancelled";
        _statusDetail =
            "The run was cancelled before Engine results were adopted. " +
            "No partial findings are shown; run again for a complete result.";
        RetainHistoricalCandidates();
        NotifyState();
    }

    internal void SimulateRunFailureForTest(string title, string detail) =>
        Fail(title, detail);

    internal void SimulateRunCancelledForTest() => MarkRunCancelled();

    private void NotifyState()
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(string.Empty));
        _runCommand.RaiseCanExecuteChanged();
        _cancelCommand.RaiseCanExecuteChanged();
        _openReportCommand.RaiseCanExecuteChanged();
        _zoomCommand.RaiseCanExecuteChanged();
        _revalidateCommand.RaiseCanExecuteChanged();
        _zoomCandidatePositiveCommand.RaiseCanExecuteChanged();
        _zoomCandidateNegativeCommand.RaiseCanExecuteChanged();
        _nextPageCommand.RaiseCanExecuteChanged();
        _previousPageCommand.RaiseCanExecuteChanged();
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
        CancelCandidateNavigation();
        _runCancellation?.Dispose();
        _runCancellation = null;
        CancelOverlayUpdate();
        _pipeline.CancelCurrent();
        _boardOverlay.Dispose();
        _capturedReview = null;
        _verifiedZoom = null;
        _publication.Supersede();
        _drawingZoom = null;
        _drawingSource = null;
        _lifetime.Dispose();
    }
}
