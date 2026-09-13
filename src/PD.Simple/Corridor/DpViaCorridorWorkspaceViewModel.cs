using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CircuitHub.AllegroBridge.Engine.Live;
using PD.Simple.Corridor;
using PD.Simple;

namespace PD.Simple.Corridor;

/// <summary>Owns DPVC setup and captured results; the reusable tool module owns screening policy.</summary>
public sealed class DpViaCorridorWorkspaceViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly BridgeSession _session;
    private readonly IDpViaCorridorService _corridor;
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
    private string _requestReport = string.Empty;
    private DpViaCorridorAnalysis? _analysis;
    private DpViaCorridorResult? CurrentResult => _analysis?.Result;
    private DpViaCorridorFinding? _selectedFinding;
    private bool _followSelection = true;
    private bool _isNavigating;
    private bool _disposed;
    private long _selectionEpoch;
    private string _navigationError = string.Empty;
    private DpViaCorridorNativeCapture? _nativeCapture;
    private DpViaCorridorZoomResult? _verifiedZoom;
    private sealed record NavigationContext(long Epoch, long Generation, string Session,
        string Design, string Report, string FindingId, string Layer);

    public DpViaCorridorWorkspaceViewModel(BridgeSession session)
    {
        _session = session;
        _corridor = session;
        _state = session.State;
        _runCommand = new RelayCommand(Run, () => CanRun);
        _openReportCommand = new RelayCommand(OpenReport, () => CanOpenReport);
        _zoomCommand = new RelayCommand(ZoomSelectedFinding, () => CanZoom);
        ClearFiltersCommand = new RelayCommand(() => { SearchText = string.Empty; RiskFilter = "All risks"; });
        VisibleFindings = new ReadOnlyObservableCollection<DpViaCorridorFinding>(_visibleFindings);
        session.StateChanged += Session_StateChanged;
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
    public bool IsResultCurrent => CurrentResult is not null && !_settingsChanged && _state.IsReady &&
        CurrentResult.BoardGeneration == _state.BoardGeneration &&
        _analysis!.IsCurrentFor(_state.SessionId, _state.BoardGeneration, _state.CatalogGeneration) &&
        string.Equals(CurrentResult.Design, _state.Design, StringComparison.Ordinal);
    public bool CanRun => !_isBusy && !_isNavigating && _state.IsReady &&
        _state.UnavailableDetail is null && InputError.Length == 0 &&
        _state.CanRunCorridor;
    public bool CanOpenReport => CurrentResult is not null && !_isBusy &&
        string.Equals(CurrentResult.ReportPath, _requestReport, StringComparison.OrdinalIgnoreCase) &&
        File.Exists(_requestReport);
    public string InputError => !TryMargin(out _)
        ? "Enter a margin from 0 to 50 mils."
        : ModuleFilter.Length > 64 || ModuleFilter.Any(char.IsControl)
            ? "Use a native module instance name of up to 64 characters." : string.Empty;
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
              : "Reads coherent board inputs and screens them in the reusable C# tool module.");
    public string PairCountDisplay => CurrentResult?.PairCount.ToString("N0") ?? "—";
    public string CorridorCountDisplay => CurrentResult?.CorridorCount.ToString("N0") ?? "—";
    public string FindingCountDisplay => CurrentResult?.FindingCount.ToString("N0") ?? "—";
    public string RiskSummary => CurrentResult is null ? "Risk labels are advisory, not an SI sign-off." :
        $"{CurrentResult.CriticalCount:N0} critical · {CurrentResult.MediumCount:N0} medium · {CurrentResult.LowCount:N0} low";
    public string ResultProvenance => CurrentResult is null ? "Illustration only · no board results yet" :
        $"{(IsResultCurrent ? "Captured result" : "Previous result")}" +
        $" · generation {CurrentResult.BoardGeneration} · source {CurrentResult.SourceUnits} · display mils";
    public string ResultScope => CurrentResult is null ? "Results will appear here after analysis." :
        _settingsChanged ? "Settings changed. Run again to analyze with these options." :
        !IsResultCurrent ? "The board or tool catalog changed. These results are historical; run again." :
        !CurrentResult.HasCompleteInputs ? "Incomplete native inputs. Findings are review information only; a clear result cannot be established. See the report." :
        CurrentResult.Truncated ? $"Showing a bounded capture of {CurrentResult.Findings.Count:N0} of {CurrentResult.FindingCount:N0} crossings. The report contains the full analysis." :
        "Captured analysis, not a live board view. Risk classifications are advisory.";
    public string FindingListSummary => CurrentResult is null ? "No analysis yet" :
        $"{_visibleFindings.Count:N0} shown · {CurrentResult.FindingCount:N0} total";
    public string EmptyResultsTitle => CurrentResult is null ? "Run a check to review crossings" :
        !CurrentResult.HasCompleteInputs ? "Review required: incomplete inputs" :
        CurrentResult.FindingCount == 0 ? "No crossings reported by screening" : "No matching crossings";
    public string EmptyResultsDetail => CurrentResult is null ?
        "The checker will return the affected pair, aggressor, layer and captured geometry." :
        !CurrentResult.HasCompleteInputs ? "Required native data was unavailable. The absence of displayed crossings is not a pass; read the coverage warnings in the report." :
        CurrentResult.FindingCount == 0 ? "This run found no corridor crossings in its analyzed scope. This is not a full SI sign-off." :
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
        _settingsChanged = CurrentResult is not null;
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
            LiveDesignScene source = _analysis?.LiveScene ??
                throw new InvalidOperationException("The current corridor analysis has no live Engine capture.");
            _session.SetDpViaCorridorOverlay(new(
                _state.SessionId,
                _verifiedZoom,
                _selectedFinding,
                source,
                _selectionEpoch));
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

    private async void ZoomSelectedFinding()
    {
        if (!CanZoom || _analysis is null || _selectedFinding is null)
        {
            return;
        }

        var analysis = _analysis;
        var finding = _selectedFinding;
        InvalidateNativeCapture();
        var context = new NavigationContext(_selectionEpoch, _state.BoardGeneration, _state.SessionId,
            _state.Design, analysis.Result.ReportPath, finding.Id, finding.Layer);
        _isNavigating = true;
        NotifyState();
        try
        {
            var zoom = await _corridor.NavigateAsync(analysis, finding);
            if (!IsNavigationCurrent(context))
            {
                return;
            }
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

    private bool IsNavigationCurrent(NavigationContext context) => !_disposed &&
        context.Epoch == _selectionEpoch && IsResultCurrent && _session.HasLiveNativeSession &&
        _state.BoardGeneration == context.Generation && _state.SessionId == context.Session &&
        _state.Design == context.Design && CurrentResult?.ReportPath == context.Report &&
        _selectedFinding?.Id == context.FindingId && _selectedFinding.Layer == context.Layer;

    private async void Run()
    {
        if (!CanRun || !TryMargin(out var margin))
        {
            return;
        }

        bool dispatched = false;
        try
        {
            InvalidateNativeCapture();
            var reportDirectory = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "PD-Simple", "Reports", "DPVC");
            Directory.CreateDirectory(reportDirectory);
            _requestReport = Path.Combine(reportDirectory, $"dpvc-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.rpt");
            _isBusy = true;
            _hasProblem = false;
            _settingsChanged = true;
            _statusTitle = "Checking in Allegro";
            _statusDetail = "The SDK is collecting a coherent board capture; managed screening follows. Missing required data remains explicit.";
            NotifyState();
            dispatched = true;
            var analysis = await _corridor.AnalyzeAsync(
                new DpViaCorridorOptions(margin, ModuleFilter.Trim(), IncludeUnused), _requestReport);
            if (_disposed)
            {
                return;
            }
            if (!_state.IsReady || !analysis.IsCurrentFor(
                    _state.SessionId, _state.BoardGeneration, _state.CatalogGeneration) ||
                !string.Equals(_state.Design, analysis.Result.Design, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The board or tool catalog changed during analysis. Run again on the current board.");
            }
            _isBusy = false;
            AdoptResult(analysis);
        }
        catch (Exception error)
        {
            if (!_disposed)
            {
                string title = !dispatched ? "Could not start check" :
                    error is InvalidDataException ? "Result could not be verified" : "Check did not complete";
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
        var capture = analysis.Result;
        _settingsChanged = false;
        _hasProblem = !capture.HasCompleteInputs;
        _statusTitle = !capture.HasCompleteInputs ? "Review required: incomplete inputs" :
            capture.FindingCount == 0 ? "No crossings reported by screening" : "Crossings ready to review";
        _statusDetail = !capture.HasCompleteInputs
            ? $"{capture.CoverageWarnings.Count} native-data limitations are listed in the report. No clear/pass conclusion is permitted. " +
                capture.CoverageWarnings[0]
            : "Managed screening and report are complete. This is not a clearance or SI simulation. Select a crossing for fresh native revalidation.";
        RefreshFindings();
        NotifyState();
        FollowSelectedFinding();
    }

    private void RefreshFindings()
    {
        var selectedId = _selectedFinding?.Id;
        _visibleFindings.Clear();
        foreach (var finding in CurrentResult?.Findings ?? [])
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
            _state.SessionId != state.SessionId || _state.Design != state.Design ||
            _state.CatalogGeneration != state.CatalogGeneration ||
            _state.ConnectionGeneration != state.ConnectionGeneration;
        _state = state;
        if (contextChanged)
        {
            InvalidateNativeCapture();
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
    }
}
