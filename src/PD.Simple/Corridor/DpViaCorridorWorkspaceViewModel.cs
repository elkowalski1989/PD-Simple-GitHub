using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using PD.Bridge;
using PD.Simple.Corridor;
using PD.Simple;

namespace PD.Simple.Corridor;

/// <summary>Owns DPVC setup and captured results; the native checker owns analysis.</summary>
public sealed class DpViaCorridorWorkspaceViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly BridgeSession _session;
    private readonly RelayCommand _runCommand;
    private readonly RelayCommand _openReportCommand;
    private readonly RelayCommand _zoomCommand;
    private readonly ObservableCollection<DpViaCorridorFinding> _visibleFindings = [];
    private SimpleSessionState _state;
    private string _marginMils = "0";
    private string _moduleFilter = string.Empty;
    private bool _includeUnused;
    private string _searchText = string.Empty;
    private string _riskFilter = "All risks";
    private string _statusTitle = "Connect to Allegro";
    private string _statusDetail = "Open PD from Allegro with a board loaded to run this check.";
    private bool _isBusy;
    private bool _settingsChanged;
    private bool _hasProblem;
    private long _pendingRequestId;
    private long _requestGeneration;
    private string _requestSession = string.Empty;
    private string _requestDesign = string.Empty;
    private string _requestReport = string.Empty;
    private string _resultSession = string.Empty;
    private DpViaCorridorResult? _result;
    private DpViaCorridorFinding? _selectedFinding;
    private bool _followSelection = true;
    private bool _isNavigating;
    private bool _disposed;
    private long _selectionEpoch;
    private long _pendingZoomRequestId;
    private string _navigationError = string.Empty;
    private NavigationContext? _navigationContext;
    private DpViaCorridorNativeCapture? _nativeCapture;
    private DpViaCorridorZoomResult? _verifiedZoom;
    private sealed record NavigationContext(long Epoch, long Generation, string Session,
        string Design, string Report, string FindingId, string Layer);

    public DpViaCorridorWorkspaceViewModel(BridgeSession session)
    {
        _session = session;
        _state = session.State;
        _runCommand = new RelayCommand(Run, () => CanRun);
        _openReportCommand = new RelayCommand(OpenReport, () => CanOpenReport);
        _zoomCommand = new RelayCommand(ZoomSelectedFinding, () => CanZoom);
        ClearFiltersCommand = new RelayCommand(() => { SearchText = string.Empty; RiskFilter = "All risks"; });
        VisibleFindings = new ReadOnlyObservableCollection<DpViaCorridorFinding>(_visibleFindings);
        session.StateChanged += Session_StateChanged;
        session.OperationChanged += Session_OperationChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ICommand RunCommand => _runCommand;
    public ICommand OpenReportCommand => _openReportCommand;
    public ICommand ZoomCommand => _zoomCommand;
    public ICommand ClearFiltersCommand
    {
        get;
    }
    public IReadOnlyList<string> RiskOptions { get; } = ["All risks", "CRITICAL", "MEDIUM", "LOW"];
    public ReadOnlyObservableCollection<DpViaCorridorFinding> VisibleFindings
    {
        get;
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
                InvalidateNativeCapture();
                Changed(nameof(SelectedFindingTitle));
                Changed(nameof(SelectedFindingDetail));
                NotifyState();
                FollowSelectedFinding();
            }
        }
    }

    public DpViaCorridorResult? Result => _result;
    public bool HasResult => _result is not null;
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
    public DpViaCorridorNativeCapture? NativeCapture => _nativeCapture;
    public DpViaCorridorZoomResult? VerifiedZoom => _verifiedZoom;
    public bool HasNativeCapture => _nativeCapture is not null;
    private bool _showHighlighting = true;
    private bool _workspaceVisible;
    private string _boardOverlayError = string.Empty;
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
    public string BoardOverlayStatus => _boardOverlayError.Length > 0 ? _boardOverlayError :
        ShowHighlighting ? "Also shown on the active Allegro canvas · captured finding; re-run after edits." :
            "Highlighting off · preview and PNG export use raw pixels.";
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
    public bool CanZoom => !_disposed && !_isBusy && !_isNavigating && _session.HasLiveNativeSession &&
        IsResultCurrent && _selectedFinding is not null && _state.UnavailableDetail is null;
    public string PreviewTitle => HasNativeCapture ? "Captured Allegro view" : "Measured corridor geometry";
    public string PreviewStatus => _navigationError.Length > 0 ? _navigationError : _isNavigating
        ? "Zooming in Allegro and capturing its current view…"
        : _nativeCapture is { } image ? $"Captured {image.CapturedAt.LocalDateTime:HH:mm:ss} · selected finding layer: {image.Layer}. Highlighting marks the measured corridor, via centers and intrusion—not copper outlines. Wheel to magnify; Fit to reset."
        : !_session.HasLiveNativeSession ? "Measured fallback · native view requires a live Allegro session."
        : !IsResultCurrent ? "Run a current check before navigating in Allegro."
        : "Select a crossing to capture its current Allegro view. No copper edits.";
    public bool HasProblem => _hasProblem;
    public bool IsResultCurrent => _result is not null && !_settingsChanged && _state.IsReady &&
        _result.BoardGeneration == _state.BoardGeneration &&
        string.Equals(_resultSession, _state.SessionId, StringComparison.Ordinal) &&
        string.Equals(_result.Design, _state.Design, StringComparison.Ordinal);
    public bool CanRun => !_isBusy && !_isNavigating && _state.IsReady &&
        _state.UnavailableDetail is null && InputError.Length == 0 &&
        _state.CanRunCorridor;
    public bool CanOpenReport => _result is not null && !_isBusy &&
        string.Equals(_result.ReportPath, _requestReport, StringComparison.OrdinalIgnoreCase) &&
        File.Exists(_requestReport);
    public string InputError => !TryMargin(out _)
        ? "Enter a margin from 0 to 50 mils."
        : ModuleFilter.Length > 64 || ModuleFilter.Any(char.IsControl)
            ? "Use a component reference of up to 64 characters." : string.Empty;
    public string BoardDisplay => _state.IsReady ? _state.Design : "No live board";
    public string StatusTitle => _statusTitle;
    public string StatusDetail => _statusDetail;
    public string RunLabel => _isBusy ? "Checking in Allegro…" : HasResult ? "Run again" : "Run corridor check";
    public string RunAvailability => InputError.Length > 0 ? InputError : _isNavigating
        ? "Wait for native navigation and image capture to finish."
        : _isBusy
        ? "Keep the board open. This checker has no mid-run cancellation."
        : !_state.IsReady ? "A connected Allegro board is required."
        : _state.UnavailableDetail ??
          (!_state.CanRunCorridor
              ? "The DP via corridor checker is unavailable in this session."
              : "Runs the existing checker without its legacy settings form.");
    public string PairCountDisplay => _result?.PairCount.ToString("N0") ?? "—";
    public string CorridorCountDisplay => _result?.CorridorCount.ToString("N0") ?? "—";
    public string FindingCountDisplay => _result?.FindingCount.ToString("N0") ?? "—";
    public string RiskSummary => _result is null ? "Risk labels are advisory, not an SI sign-off." :
        $"{_result.CriticalCount:N0} critical · {_result.MediumCount:N0} medium · {_result.LowCount:N0} low";
    public string ResultProvenance => _result is null ? "Illustration only · no board results yet" :
        $"{(IsResultCurrent ? "Captured result" : "Previous result")}" +
        $" · generation {_result.BoardGeneration} · source {_result.SourceUnits} · display mils";
    public string ResultScope => _result is null ? "Results will appear here after analysis." :
        _settingsChanged ? "Settings changed. Run again to analyze with these options." :
        !IsResultCurrent ? "The board context changed. These results are historical; run again." :
        _result.Truncated ? $"Showing a bounded capture of {_result.Findings.Count:N0} of {_result.FindingCount:N0} crossings. The report contains the full analysis." :
        "Captured analysis, not a live board view. Risk classifications are advisory.";
    public string FindingListSummary => _result is null ? "No analysis yet" :
        $"{_visibleFindings.Count:N0} shown · {_result.FindingCount:N0} total";
    public string EmptyResultsTitle => _result is null ? "Run a check to review crossings" :
        _result.FindingCount == 0 ? "No crossings reported" : "No matching crossings";
    public string EmptyResultsDetail => _result is null ?
        "The checker will return the affected pair, aggressor, layer and captured geometry." :
        _result.FindingCount == 0 ? "This run found no corridor crossings in its analyzed scope. This is not a full SI sign-off." :
        "Change the search or risk filter to see other captured crossings.";
    public string SelectedFindingTitle => _selectedFinding?.PairName ?? "What the checker looks for";
    public string SelectedFindingDetail => _selectedFinding is null ?
        "Foreign conductors entering the protected corridor between P and N via centers." :
        $"{_selectedFinding.AggressorNet} · {_selectedFinding.ObjectType} · {_selectedFinding.Layer}";

    private bool TryMargin(out decimal margin) => decimal.TryParse(MarginMils,
        NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
        CultureInfo.InvariantCulture, out margin) && margin is >= 0 and <= 50;

    private void InputsChanged()
    {
        _settingsChanged = _result is not null;
        InvalidateNativeCapture();
        NotifyState();
    }

    private void InvalidateNativeCapture()
    {
        _session.SetDpViaCorridorOverlay(null);
        _boardOverlayError = string.Empty;
        ++_selectionEpoch;
        _nativeCapture = null;
        _verifiedZoom = null;
        _navigationError = string.Empty;
    }

    private void UpdateBoardOverlay()
    {
        _boardOverlayError = string.Empty;
        if (_disposed || !_workspaceVisible || !ShowHighlighting || !HasNativeCapture ||
            _verifiedZoom is null || _selectedFinding is null || !IsResultCurrent || !_session.HasLiveNativeSession)
        {
            _session.SetDpViaCorridorOverlay(null);
            return;
        }
        try
        {
            _session.SetDpViaCorridorOverlay(new(_state.SessionId, _verifiedZoom, _selectedFinding));
        }
        catch (InvalidOperationException error)
        {
            _session.SetDpViaCorridorOverlay(null);
            _boardOverlayError = "Allegro highlighting unavailable: " + error.Message;
        }
    }

    private void FollowSelectedFinding()
    {
        if (_followSelection && CanZoom && _nativeCapture is null && _navigationError.Length == 0)
        {
            ZoomSelectedFinding();
        }
    }

    private void ZoomSelectedFinding()
    {
        if (!CanZoom || _result is null || _selectedFinding is null)
        {
            return;
        }

        InvalidateNativeCapture();
        var context = new NavigationContext(_selectionEpoch, _state.BoardGeneration, _state.SessionId,
            _state.Design, _result.ReportPath, _selectedFinding.Id, _selectedFinding.Layer);
        try
        {
            _navigationContext = context;
            _isNavigating = true;
            _pendingZoomRequestId = _session.ZoomDpViaCorridor(context.Report, context.FindingId);
            if (_pendingZoomRequestId <= 0)
            {
                throw new InvalidOperationException("Native zoom returned no queued request identity.");
            }
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            _pendingZoomRequestId = 0;
            _navigationContext = null;
            _isNavigating = false;
            _navigationError = "Native preview unavailable: " + error.Message;
        }
        NotifyState();
    }

    private bool IsNavigationCurrent(NavigationContext context) => !_disposed &&
        context.Epoch == _selectionEpoch && IsResultCurrent && _session.HasLiveNativeSession &&
        _state.BoardGeneration == context.Generation && _state.SessionId == context.Session &&
        _state.Design == context.Design && _result?.ReportPath == context.Report &&
        _selectedFinding?.Id == context.FindingId && _selectedFinding.Layer == context.Layer;

    private async Task CompleteZoomAsync(BridgeOperationResult operation, NavigationContext context)
    {
        try
        {
            if (!IsNavigationCurrent(context))
            {
                return;
            }

            if (operation.Outcome != BridgeOutcome.Succeeded)
            {
                throw new InvalidOperationException(operation.Message);
            }

            if (operation.BoardGeneration != context.Generation)
            {
                throw new InvalidDataException("The board changed during native navigation.");
            }

            var zoom = DpViaCorridorZoomResult.Parse(operation.ResultPayloadJson ?? string.Empty,
                context.Generation, context.Design, context.Report, context.FindingId, context.Layer);
            var capture = await _session.CaptureDpViaCorridorNativeAsync(zoom);
            if (!IsNavigationCurrent(context))
            {
                return;
            }

            if (capture.FindingId != context.FindingId || capture.Layer != context.Layer ||
                capture.Bounds != zoom.ActualBounds || capture.Image.PixelWidth <= 0 || capture.Image.PixelHeight <= 0)
            {
                throw new InvalidDataException("Native image does not match the verified finding and viewport.");
            }

            _verifiedZoom = zoom;
            _nativeCapture = capture;
            _navigationError = string.Empty;
            UpdateBoardOverlay();
        }
        catch (Exception error)
        {
            if (IsNavigationCurrent(context))
            {
                _navigationError = "Native preview unavailable: " + error.Message;
            }
        }
        finally
        {
            _navigationContext = null;
            _isNavigating = false;
            if (!_disposed)
            {
                NotifyState();
                // A newer selection waits for this request/capture to finish;
                // stale pixels and stale failures never become its preview.
                if (context.Epoch != _selectionEpoch)
                {
                    FollowSelectedFinding();
                }
            }
        }
    }

    private void Run()
    {
        if (!CanRun || !TryMargin(out var margin))
        {
            return;
        }

        try
        {
            InvalidateNativeCapture();
            var reportDirectory = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "PD-Simple", "Reports", "DPVC");
            Directory.CreateDirectory(reportDirectory);
            _requestReport = Path.Combine(reportDirectory, $"dpvc-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.rpt");
            _requestGeneration = _state.BoardGeneration;
            _requestSession = _state.SessionId;
            _requestDesign = _state.Design;
            _pendingRequestId = _session.RunDpViaCorridor(margin, ModuleFilter.Trim(), IncludeUnused, _requestReport);
            _isBusy = true;
            _hasProblem = false;
            _settingsChanged = true;
            _statusTitle = "Checking in Allegro";
            _statusDetail = "The native checker is analyzing via corridors. Results are published only when the report is complete.";
            NotifyState();
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            Fail("Could not start check", error.Message);
        }
    }

    // Offline GUI acceptance freezes request identity, then replays receipts
    // through the production consumer. It never dispatches a native operation.
    internal void BeginVisualTestRequest(long requestId, string reportPath)
    {
        if (!CanRun || requestId <= 0)
        {
            throw new InvalidOperationException("The visual request requires valid, idle setup.");
        }

        _requestReport = reportPath;
        _requestGeneration = _state.BoardGeneration;
        _requestSession = _state.SessionId;
        _requestDesign = _state.Design;
        _pendingRequestId = requestId;
        _isBusy = true;
        _hasProblem = false;
        _settingsChanged = true;
        _statusTitle = "Checking in Allegro";
        _statusDetail = "SAMPLE DATA / UI VERIFICATION — replaying the result lifecycle; no native analysis runs.";
        NotifyState();
    }

    internal void ReplayVisualTestOperation(BridgeOperationResult result) =>
        Session_OperationChanged(this, result);

    internal void ReplayVisualTestState(SimpleSessionState state) =>
        Session_StateChanged(this, state);

    private void Session_OperationChanged(object? sender, BridgeOperationResult result)
    {
        if (_pendingZoomRequestId != 0 && result.RequestId == _pendingZoomRequestId)
        {
            if (result.Outcome == BridgeOutcome.Accepted)
            {
                return;
            }

            _pendingZoomRequestId = 0;
            if (_navigationContext is { } context)
            {
                _ = CompleteZoomAsync(result, context);
            }

            return;
        }
        if (_pendingRequestId == 0 || result.RequestId != _pendingRequestId)
        {
            return;
        }

        if (result.Outcome == BridgeOutcome.Accepted)
        {
            return; // Queue acceptance is not an analysis result.
        }

        _pendingRequestId = 0;
        _isBusy = false;
        if (result.Outcome != BridgeOutcome.Succeeded)
        {
            Fail("Check did not complete", result.Message);
            return;
        }
        try
        {
            if (!_state.IsReady || result.BoardGeneration != _requestGeneration ||
                _state.BoardGeneration != _requestGeneration ||
                !string.Equals(_state.SessionId, _requestSession, StringComparison.Ordinal) ||
                !string.Equals(_state.Design, _requestDesign, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The board changed during analysis. Run again on the current board.");
            }

            var capture = DpViaCorridorResult.Parse(result.ResultPayloadJson ?? string.Empty,
                _requestGeneration, _requestDesign);
            if (!string.Equals(Path.GetFullPath(capture.ReportPath), Path.GetFullPath(_requestReport),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The returned report does not match this analysis request.");
            }

            AdoptResult(capture, _requestSession);
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or NotSupportedException)
        {
            Fail("Result could not be verified", error.Message);
        }
    }

    private void AdoptResult(DpViaCorridorResult capture, string sessionId)
    {
        _result = capture;
        _resultSession = sessionId;
        _settingsChanged = false;
        _hasProblem = false;
        _statusTitle = capture.FindingCount == 0 ? "No crossings reported" : "Crossings ready to review";
        _statusDetail = capture.NavigatorWritten ?
            "Analysis and report are complete. Select a crossing to inspect it in Allegro." :
            "Analysis and report are complete, but the legacy navigator data could not be written.";
        RefreshFindings();
        NotifyState();
        FollowSelectedFinding();
    }

    private void RefreshFindings()
    {
        var selectedId = _selectedFinding?.Id;
        _visibleFindings.Clear();
        foreach (var finding in _result?.Findings ?? [])
        {
            if (RiskFilter != "All risks" && finding.Risk != RiskFilter)
            {
                continue;
            }

            if (SearchText.Length > 0 && !new[] { finding.PairName, finding.AggressorNet, finding.Layer, finding.Category }
                    .Any(text => text.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            _visibleFindings.Add(finding);
        }
        SelectedFinding = _visibleFindings.FirstOrDefault(item => item.Id == selectedId) ?? _visibleFindings.FirstOrDefault();
        Changed(nameof(HasVisibleFindings));
        Changed(nameof(FindingListSummary));
        Changed(nameof(EmptyResultsTitle));
        Changed(nameof(EmptyResultsDetail));
    }

    private void Session_StateChanged(object? sender, SimpleSessionState state)
    {
        var contextChanged = _state.IsReady != state.IsReady || _state.BoardGeneration != state.BoardGeneration ||
            _state.SessionId != state.SessionId || _state.Design != state.Design;
        _state = state;
        if (contextChanged)
        {
            InvalidateNativeCapture();
        }

        if (!_isBusy)
        {
            if (_result is not null && !IsResultCurrent)
            {
                _statusTitle = "Previous result";
                _statusDetail = ResultScope;
            }
            else if (_result is null && !_hasProblem)
            {
                _statusTitle = state.IsReady ? "Ready to check" : "Connect to Allegro";
                _statusDetail = RunAvailability;
            }
        }
        NotifyState();
    }

    private void OpenReport()
    {
        if (!CanOpenReport)
        {
            return;
        }

        try
        {
            var start = new ProcessStartInfo("notepad.exe") { UseShellExecute = false };
            start.ArgumentList.Add(_requestReport);
            Process.Start(start);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
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
        // One feature owner invalidates its small presentation projection.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        _runCommand.RaiseCanExecuteChanged();
        _openReportCommand.RaiseCanExecuteChanged();
        _zoomCommand.RaiseCanExecuteChanged();
    }
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string name = "")
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
        _disposed = true;
        InvalidateNativeCapture();
        _session.StateChanged -= Session_StateChanged;
        _session.OperationChanged -= Session_OperationChanged;
    }
}
