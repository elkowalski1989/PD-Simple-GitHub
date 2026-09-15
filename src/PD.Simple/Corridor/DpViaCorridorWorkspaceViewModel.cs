using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using CircuitHub.AllegroBridge.Engine.Drawing;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Wpf;
using CircuitHub.AllegroBridge.Wpf.Engine;

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
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly RelayCommand _runCommand;
    private readonly RelayCommand _openReportCommand;
    private readonly RelayCommand _zoomCommand;
    private readonly ObservableCollection<DpViaCorridorFinding> _visibleFindings = [];
    private EngineSessionSnapshot _state;
    private CancellationTokenSource? _overlayUpdate;
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
    private long _selectionEpoch;
    private string _navigationError = string.Empty;
    private DpViaCorridorReviewCapture? _capturedReview;
    private DpViaCorridorZoomResult? _verifiedZoom;
    private bool _showHighlighting = true;
    private bool _workspaceVisible;
    private string _boardOverlayError = string.Empty;

    private DpViaCorridorResult? CurrentResult => _analysis?.Result;

    private sealed record NavigationContext(
        long Epoch,
        WorkspaceDocumentIdentity Document,
        Guid CaptureId,
        string Report,
        string FindingId,
        string Layer);

    public DpViaCorridorWorkspaceViewModel(
        AllegroEngineSession session,
        EngineWpfPresentation presentation)
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
        _dispatcher = Dispatcher.CurrentDispatcher;
        _state = _session.State;
        _runCommand = new RelayCommand(Run, () => CanRun);
        _openReportCommand = new RelayCommand(OpenReport, () => CanOpenReport);
        _zoomCommand = new RelayCommand(ZoomSelectedFinding, () => CanZoom);
        ClearFiltersCommand = new RelayCommand(() =>
        {
            SearchText = string.Empty;
            RiskFilter = "All risks";
        });
        VisibleFindings =
            new ReadOnlyObservableCollection<DpViaCorridorFinding>(_visibleFindings);
        _session.StateChanged += Session_StateChanged;
        _presentation.StateChanged += Presentation_StateChanged;
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
                InvalidateCapturedReview();
                Changed(nameof(SelectedFindingTitle));
                Changed(nameof(SelectedFindingDetail));
                NotifyState();
                FollowSelectedFinding();
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

    public string BoardOverlayStatus =>
        _boardOverlayError.Length > 0
            ? _boardOverlayError
            : ShowHighlighting
                ? "Also shown through the Engine/WPF overlay · captured finding; re-run after edits."
                : "Highlighting off · review and PNG export use raw captured pixels.";

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
                    : !HasReadySession
                        ? "Measured fallback · captured review requires a ready Engine session."
                        : !IsResultCurrent
                            ? "Run a current check before navigating in Allegro."
                            : "Select a crossing to capture its current Allegro review. No copper edits.";

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
                $"{_selectedFinding.ObjectType} · {_selectedFinding.Layer}";

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
        return capability is null || capability.Availability == EngineCapabilityAvailability.Available
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

    private void InvalidateCapturedReview()
    {
        CancelOverlayUpdate();
        _boardOverlay.Clear();
        _boardOverlayError = string.Empty;
        ++_selectionEpoch;
        _capturedReview = null;
        _verifiedZoom = null;
        _navigationError = string.Empty;
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
            !IsResultCurrent ||
            !_presentation.State.IsAvailable)
        {
            _boardOverlay.Clear();
            return;
        }

        var overlay = new DpViaCorridorBoardOverlay(
            zoom,
            finding,
            source,
            capture.DrawingSource.ForLive(source.Scene),
            capture.DrawingSource.Drawings.Revision);
        long epoch = _selectionEpoch;
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
        DpViaCorridorReviewCapture capture,
        long epoch,
        CancellationTokenSource cancellation)
    {
        try
        {
            await _boardOverlay.PresentAsync(overlay, cancellation.Token);
            if (IsOverlayContextCurrent(capture, epoch))
            {
                _boardOverlayError = string.Empty;
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
            if (IsOverlayContextCurrent(capture, epoch))
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
        DpViaCorridorReviewCapture capture,
        long epoch) =>
        !_disposed &&
        epoch == _selectionEpoch &&
        ReferenceEquals(_capturedReview, capture) &&
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
        if (_followSelection &&
            CanZoom &&
            _capturedReview is null &&
            _navigationError.Length == 0)
        {
            ZoomSelectedFinding();
        }
    }

    private async void ZoomSelectedFinding()
    {
        if (!CanZoom ||
            _analysis?.LiveScene is not { } source ||
            _selectedFinding is not { } finding)
        {
            return;
        }

        DpViaCorridorAnalysis analysis = _analysis;
        InvalidateCapturedReview();
        var context = new NavigationContext(
            _selectionEpoch,
            analysis.Document,
            source.Scene.Identity.CaptureId,
            analysis.Result.ReportPath,
            finding.Id,
            finding.Layer);
        _isNavigating = true;
        NotifyState();
        try
        {
            DpViaCorridorZoomResult zoom = await _corridor.NavigateAsync(
                analysis,
                finding,
                _lifetime.Token);
            if (!IsNavigationCurrent(context))
            {
                return;
            }

            DpViaCorridorDrawingSource drawingSource =
                BoardOverlayDrawingPolicy.CorridorSource(
                source.Scene,
                finding,
                context.Epoch);
            DrawingScene drawings = drawingSource.ForReview(source.Scene);
            AllegroReviewFrame review = await _presentation.CaptureReviewAsync(
                source,
                drawings,
                _lifetime.Token);
            if (!IsNavigationCurrent(context))
            {
                return;
            }

            DpViaCorridorReviewCapture capture =
                DpViaCorridorReviewCapture.Create(
                    review,
                    source,
                    drawingSource,
                    zoom);
            _verifiedZoom = zoom;
            _capturedReview = capture;
            _navigationError = string.Empty;
            UpdateBoardOverlay();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (IsNavigationCurrent(context))
            {
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
                if (context.Epoch != _selectionEpoch)
                {
                    FollowSelectedFinding();
                }
            }
        }
    }

    private bool IsNavigationCurrent(NavigationContext context) =>
        !_disposed &&
        context.Epoch == _selectionEpoch &&
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
        _statusDetail = !result.HasCompleteInputs
            ? $"{result.CoverageWarnings.Count} Engine-data limitations are " +
                "listed in the report. No clear/pass conclusion is permitted. " +
                result.CoverageWarnings[0]
            : "Managed screening and report are complete. This is not a clearance " +
                "or SI simulation. Select a crossing for fresh Engine navigation " +
                "and WPF capture.";
        RefreshFindings();
        NotifyState();
        FollowSelectedFinding();
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
                CancelOverlayUpdate();
                _boardOverlay.Clear();
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
            else if (_capturedReview is not null)
            {
                UpdateBoardOverlay();
            }
            NotifyState();
        });
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
        _lifetime.Cancel();
        CancelOverlayUpdate();
        _boardOverlay.Dispose();
        _capturedReview = null;
        _verifiedZoom = null;
        _lifetime.Dispose();
    }
}
