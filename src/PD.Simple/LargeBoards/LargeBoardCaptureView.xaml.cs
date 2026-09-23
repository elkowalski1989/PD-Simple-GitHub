using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.PcbTools;

namespace PD.Simple.LargeBoards;

internal sealed class BoundedBoardSceneReadyEventArgs : EventArgs
{
    internal BoundedBoardSceneReadyEventArgs(
        DesignScene scene,
        SceneQuery query,
        int pagesRead,
        long recordsSelected)
    {
        Scene = scene ?? throw new ArgumentNullException(nameof(scene));
        Query = query ?? throw new ArgumentNullException(nameof(query));
        PagesRead = pagesRead;
        RecordsSelected = recordsSelected;
    }

    internal DesignScene Scene { get; }

    internal SceneQuery Query { get; }

    internal int PagesRead { get; }

    internal long RecordsSelected { get; }
}

public partial class LargeBoardCaptureView : UserControl, IAsyncDisposable
{
    private static readonly ViaPadMeasurementSelection CorridorViaPadMeasurements =
        ViaPadMeasurementSelection.MatchingNetNameSuffixes("_P", "_N");
    private const int CorridorPageSize = 100;
    private BridgeSession? _bridge;
    private LargeBoardCaptureWorkflow<DesignScene>? _workflow;
    private CancellationTokenSource? _operation;
    private ScalableCorridorScan? _corridorScan;
    private WorkspaceDocumentIdentity? _corridorDocument;
    private CorridorDetailedEvidence? _corridorDetailedEvidence;
    private IReadOnlyList<ScalableCorridorFinding> _corridorFindings = [];
    private int _corridorPage = 1;
    private bool _embeddedBoardDocumentEnabled;
    private bool _disposed;

    public LargeBoardCaptureView()
    {
        InitializeComponent();
    }

    internal event EventHandler<BoundedBoardSceneReadyEventArgs>? BoundedSceneReady;

    internal bool EmbeddedBoardDocumentEnabled
    {
        get => _embeddedBoardDocumentEnabled;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _embeddedBoardDocumentEnabled = value;
            if (EmbeddedDocumentHint is not null)
            {
                EmbeddedDocumentHint.Text = value
                    ? "Embedded board document is on. A successful bounded replay opens in the shared Engine drawing."
                    : "Embedded board document is off. Bounded results remain on this page until you enable it in the header.";
            }
        }
    }

    internal void Attach(BridgeSession bridge, Func<string?>? nativeProductProvider = null)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_workflow is not null)
        {
            throw new InvalidOperationException("The large-board workflow is already attached.");
        }
        _bridge = bridge;
        string diagnosticsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PD-Simple",
            "diagnostics",
            "acquisition.jsonl");
        _workflow = new(
            new EngineLargeBoardCaptureGateway(bridge.Workspace, nativeProductProvider),
            new JsonLinesAcquisitionDiagnosticOwner(diagnosticsPath),
            () => bridge.EngineSession.State.Document,
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
            bridge.EngineSession.GetType().Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "unknown");
        RefreshButtons();
    }

    internal void RefreshSessionState()
    {
        if (!_disposed)
        {
            if (_corridorDocument is not null &&
                _bridge?.EngineSession.State.Document != _corridorDocument)
            {
                ClearCorridorResult();
            }
            RefreshButtons();
        }
    }

    private async void Capture_Click(object sender, RoutedEventArgs e)
    {
        if (_workflow is null || _bridge?.State.IsReady != true || _operation is not null)
        {
            StatusText.Text = "Connect PD Simple to an Allegro board before starting capture.";
            return;
        }
        using var operation = new CancellationTokenSource();
        _operation = operation;
        RefreshButtons();
        StatusText.Text = "Capturing the board into a sealed Engine store…";
        ResultText.Text = string.Empty;
        try
        {
            var request = new LargeBoardCaptureRequest(
                "large-board-tools",
                "Capture large board",
                Window.GetWindow(this)?.IsVisible == true,
                new LargeBoardCaptureBudgets
                {
                    Families =
                    [
                        DataFamily.Nets,
                        DataFamily.Modules,
                        DataFamily.Layers,
                        DataFamily.Copper,
                        DataFamily.Connectivity,
                    ],
                    CopperKinds =
                    [
                        CopperKind.Trace,
                        CopperKind.Via,
                        CopperKind.Shape,
                    ],
                    ViaPadMeasurements = CorridorViaPadMeasurements,
                    IncludeContours = false,
                });
            LargeBoardCaptureResult result = await _workflow.CaptureAsync(
                request,
                operation.Token);
            ClearCorridorResult();
            StatusText.Text = result.UserMessage;
            ResultText.Text =
                $"Correlation {result.CorrelationId} · {result.Store.StoredBytes:N0} stored bytes";
        }
        catch (LargeBoardCaptureCancelledException exception)
        {
            StatusText.Text = exception.CleanupComplete == true
                ? "Capture cancelled. Native and local cleanup completed; no partial capture was retained."
                : exception.Message;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text =
                "Capture cancelled before completion. Cleanup evidence was unavailable; review acquisition diagnostics before retrying.";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            if (ReferenceEquals(_operation, operation))
            {
                _operation = null;
            }
            RefreshButtons();
        }
    }

    private async void Corridor_Click(object sender, RoutedEventArgs e)
    {
        if (_workflow is null || _bridge?.State.IsReady != true || _operation is not null)
        {
            StatusText.Text = "Connect PD Simple and create a sealed large-board capture first.";
            return;
        }
        if (!TryCorridorOptions(out CorridorOptions? options, out string error))
        {
            StatusText.Text = error;
            return;
        }

        using var operation = new CancellationTokenSource();
        _operation = operation;
        ClearCorridorResult();
        RefreshButtons();
        StatusText.Text =
            "Planning all differential-pair vias, then reading bounded layer batches…";
        try
        {
            BridgeSession bridge = _bridge;
            WorkspaceDocumentIdentity? CurrentDocument() =>
                !_disposed && ReferenceEquals(_bridge, bridge) && bridge.State.IsReady
                    ? bridge.EngineSession.State.Document
                    : null;
            var batchOptions = new CorridorBatchOptions(
                MaximumWidthMils: 5_000,
                MaximumHeightMils: 5_000,
                MaximumMaterializedObjects: 100_000);
            var replayBudgets = new LargeBoardCorridorReplayBudgets(
                new LargeBoardReplayBudgets
                {
                    MaximumMaterializedObjects = 200_000,
                    MaximumPagesRead = 32_768,
                    MaximumRecordsRead = 2_000_000,
                },
                new LargeBoardReplayBudgets
                {
                    MaximumMaterializedObjects = 100_000,
                    MaximumPagesRead = 32_768,
                    MaximumRecordsRead = 2_000_000,
                });
            LargeBoardCorridorRunResult result = await _workflow.RunWithCurrentCaptureAsync(
                async (store, token) => await LargeBoardCorridorRunner.RunAsync(
                    store,
                    options!,
                    batchOptions,
                    replayBudgets,
                    token,
                    captureViaPadMeasurements: CorridorViaPadMeasurements),
                "Run scalable corridor check",
                Window.GetWindow(this)?.IsVisible == true,
                operation.Token);
            WorkspaceDocumentIdentity document = LargeBoardPublicationFence.RequireCurrent(
                result.CaptureIdentity,
                CurrentDocument());
            string reportDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PD-Simple",
                "Reports",
                "DPVC");
            await LargeBoardCorridorReportWriter.PublishAsync(
                result.Scan,
                result.CaptureIdentity.Design ?? result.Scan.PlanningScene.Document.Name,
                reportDirectory,
                result.CaptureIdentity,
                document,
                CurrentDocument,
                report =>
                {
                    _corridorScan = result.Scan;
                    _corridorDocument = document;
                    _corridorFindings = result.Scan.Findings;
                    _corridorPage = 1;
                    RefreshCorridorPage();
                    StatusText.Text = LargeBoardCorridorPresentation.CompletionStatus(
                        result.Scan.Findings.Count,
                        result.Scan.CorridorCount,
                        result.Scan.CoverageWarnings.Count);
                    ResultText.Text =
                        $"{result.Scan.PairCount:N0} pairs · {result.Counts.CompletedBatches:N0} bounded batches · " +
                        $"{result.Scan.CoverageWarnings.Count:N0} review warnings · " +
                        $"global replay {result.Timings.GlobalViaReplayMilliseconds:N0} ms · " +
                        $"bounded replay {result.Timings.BoundedReplayMilliseconds:N0} ms · " +
                        $"analysis {result.Timings.AnalysisMilliseconds:N0} ms · " +
                        $"report {report.ReportPath}";
                },
                operation.Token);
        }
        catch (OperationCanceledException)
        {
            ClearCorridorResult();
            StatusText.Text = "Large-board corridor check cancelled. No partial result was shown.";
        }
        catch (Exception exception)
        {
            ClearCorridorResult();
            StatusText.Text = exception.Message;
        }
        finally
        {
            if (ReferenceEquals(_operation, operation))
            {
                _operation = null;
            }
            RefreshButtons();
        }
    }

    private async void NavigateFinding_Click(object sender, RoutedEventArgs e)
    {
        if (_bridge is null || _corridorScan is null || _corridorDocument is null ||
            CorridorFindingsList.SelectedItem is not ScalableCorridorFinding finding ||
            _operation is not null)
        {
            return;
        }

        using var operation = new CancellationTokenSource();
        _operation = operation;
        RefreshButtons();
        StatusText.Text = "Matching the selected objects and reading current contour geometry…";
        EvidenceText.Text = string.Empty;
        try
        {
            if (_bridge.EngineSession.State.Document != _corridorDocument)
            {
                throw new InvalidOperationException(
                    "The Allegro board changed. Run a new large-board capture and corridor check.");
            }
            SceneQuery query = CorridorNavigation.CreateQuery(_corridorScan, finding);
            LiveRegionScene region = await _bridge.Workspace.ReadRegionAsync(
                query,
                operation.Token);
            CorridorNavigationEvidence navigation =
                CorridorNavigation.MatchFreshWitnessesWithEvidence(
                _corridorScan,
                finding,
                region,
                _corridorDocument);
            EngineViewport viewport = await _bridge.Workspace.Display.ZoomWitnessesAsync(
                region,
                region.Scene.Document.Bounds,
                new LayerId(finding.Finding.Layer),
                navigation.Witnesses,
                operation.Token);
            if (viewport.Document != _corridorDocument)
            {
                throw new InvalidDataException(
                    "Engine navigation returned a viewport for another document.");
            }
            _corridorDetailedEvidence = navigation.DetailedEvidence;
            StatusText.Text =
                "Selected objects matched; current geometry opened in Allegro. " +
                "The crossing analysis remains from the original scalar scan.";
            EvidenceText.Text = FormatDetailedEvidence(
                _corridorDetailedEvidence,
                region.LastZoomTiming);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Selected-crossing navigation cancelled.";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            if (ReferenceEquals(_operation, operation))
            {
                _operation = null;
            }
            RefreshButtons();
        }
    }

    private void CorridorSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _corridorDetailedEvidence = null;
        if (EvidenceText is not null)
        {
            EvidenceText.Text = string.Empty;
        }
        RefreshButtons();
    }

    private void CorridorSearchChanged(object sender, TextChangedEventArgs e)
    {
        _corridorPage = 1;
        RefreshCorridorPage();
    }

    private void PreviousCorridorPage_Click(object sender, RoutedEventArgs e)
    {
        _corridorPage = Math.Max(1, _corridorPage - 1);
        RefreshCorridorPage();
    }

    private void NextCorridorPage_Click(object sender, RoutedEventArgs e)
    {
        _corridorPage = Math.Min(CorridorPageCount(), _corridorPage + 1);
        RefreshCorridorPage();
    }

    private async void Replay_Click(object sender, RoutedEventArgs e)
    {
        if (_workflow is null || _operation is not null)
        {
            return;
        }
        if (!TryCreateQuery(out SceneQuery? query, out string error))
        {
            StatusText.Text = error;
            return;
        }
        using var operation = new CancellationTokenSource();
        _operation = operation;
        RefreshButtons();
        StatusText.Text = "Reading the bounded area from the sealed capture…";
        try
        {
            LargeBoardReplayResult<DesignScene> result = await _workflow.ReplayRegionAsync(
                query!,
                Window.GetWindow(this)?.IsVisible == true,
                operation.Token);
            StatusText.Text = result.UserMessage;
            ResultText.Text =
                $"Typed scene: {result.Scene.Data.Nets.Length:N0} nets · " +
                $"{result.Scene.Data.Layers.Length:N0} layers · " +
                $"{result.Scene.Data.Copper.Length:N0} copper objects";
            if (_embeddedBoardDocumentEnabled)
            {
                BoundedSceneReady?.Invoke(
                    this,
                    new BoundedBoardSceneReadyEventArgs(
                        result.Scene,
                        result.Query,
                        result.PagesRead,
                        result.RecordsSelected));
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Area replay cancelled. No clear result was produced.";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
            ResultText.Text = string.Empty;
        }
        finally
        {
            if (ReferenceEquals(_operation, operation))
            {
                _operation = null;
            }
            RefreshButtons();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancellationTokenSource? operation = _operation;
        if (operation is null)
        {
            return;
        }
        CancelButton.IsEnabled = false;
        StatusText.Text =
            "Cancellation requested. Waiting for the current operation to reach a terminal state and finish cleanup…";
        operation.Cancel();
    }

    private bool TryCreateQuery(out SceneQuery? query, out string error)
    {
        query = null;
        error = string.Empty;
        if (!TryDecimal(XMinimumInput.Text, out decimal xMinimum) ||
            !TryDecimal(YMinimumInput.Text, out decimal yMinimum) ||
            !TryDecimal(XMaximumInput.Text, out decimal xMaximum) ||
            !TryDecimal(YMaximumInput.Text, out decimal yMaximum) ||
            xMaximum <= xMinimum || yMaximum <= yMinimum)
        {
            error = "Enter a positive rectangular area using decimal mil coordinates.";
            return false;
        }
        if (!int.TryParse(ReplayBudgetInput.Text, NumberStyles.None,
                CultureInfo.InvariantCulture, out int maximumObjects) ||
            maximumObjects is < 1 or > 200_000)
        {
            error = "Replay object budget must be from 1 to 200,000.";
            return false;
        }
        ImmutableArray<LayerId> layers = string.IsNullOrWhiteSpace(LayerInput.Text)
            ? []
            : [new LayerId(LayerInput.Text.Trim())];
        query = new SceneQuery
        {
            Kind = SceneReadKind.RegionGeometry,
            Families =
            [
                DataFamily.Nets,
                DataFamily.Modules,
                DataFamily.Layers,
                DataFamily.Copper,
            ],
            CopperKinds = [CopperKind.Trace, CopperKind.Via, CopperKind.Shape],
            ViaPadMeasurements =
                ViaPadMeasurementSelection.MatchingNetNameSuffixes("_P", "_N"),
            Layers = layers,
            Region = new(
                new DesignPoint(xMinimum, yMinimum),
                new DesignPoint(xMaximum, yMaximum)),
            IncludeContours = false,
            MaximumObjects = maximumObjects,
        };
        return true;
    }

    private static bool TryDecimal(string text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    private bool TryCorridorOptions(out CorridorOptions? options, out string error)
    {
        options = null;
        error = string.Empty;
        if (!TryDecimal(CorridorMarginInput.Text, out decimal margin) || margin is < 0 or > 50)
        {
            error = "Corridor margin must be from 0 to 50 mil.";
            return false;
        }
        string module = CorridorModuleInput.Text.Trim();
        if (module.Length > 64 || module.Any(char.IsControl))
        {
            error = "Module name must be 64 printable characters or fewer.";
            return false;
        }
        options = new(
            (double)margin,
            module.Length == 0 ? null : module,
            IncludeUnusedPairsCheckBox.IsChecked == true);
        return true;
    }

    private void ClearCorridorResult()
    {
        _corridorScan = null;
        _corridorDocument = null;
        _corridorDetailedEvidence = null;
        _corridorFindings = [];
        _corridorPage = 1;
        if (CorridorFindingsList is not null)
        {
            CorridorFindingsList.ItemsSource = null;
        }
        if (CorridorPageText is not null)
        {
            CorridorPageText.Text = "0 findings";
        }
        if (EvidenceText is not null)
        {
            EvidenceText.Text = string.Empty;
        }
    }

    private static string FormatDetailedEvidence(
        CorridorDetailedEvidence evidence,
        EngineZoomTiming? zoomTiming)
    {
        string geometry = evidence.Status switch
        {
            CorridorDetailedEvidenceStatus.Complete =>
                $"Current shape contours retained: {evidence.ResolvedRegionCount:N0} resolved islands and {evidence.HoleCount:N0} holes.",
            CorridorDetailedEvidenceStatus.NotApplicable =>
                $"The current aggressor is {evidence.AggressorKind}; shape hole and island evidence does not apply.",
            _ =>
                "Current shape contours are incomplete. No current-geometry clear conclusion is permitted. " +
                string.Join("; ", evidence.Diagnostics.Take(3)),
        };
        EngineRegionTiming? timing = evidence.AcquisitionTiming;
        string timingText = timing is null
            ? string.Empty
            : $" Fresh read: native {timing.NativeReadWallMilliseconds:N0} ms, " +
                $"decode {timing.DecodingWallMilliseconds:N0} ms, " +
                $"conversion {timing.ConversionWallMilliseconds:N0} ms.";
        if (zoomTiming is not null)
        {
            timingText += $" Allegro open: {zoomTiming.ZoomWallMilliseconds:N0} ms.";
        }
        return geometry + timingText +
            $" Observation {evidence.FreshScene.Identity.CaptureId:N}; " +
            $"operation {evidence.NativeOperationId:N}; witness scope {evidence.WitnessVerificationScope}.";
    }

    private void RefreshCorridorPage()
    {
        if (CorridorFindingsList is null)
        {
            return;
        }
        string search = CorridorSearchInput?.Text.Trim() ?? string.Empty;
        ScalableCorridorFinding[] filtered = _corridorFindings
            .Where(item => search.Length == 0 || Matches(item.Finding, search))
            .ToArray();
        int pageCount = Math.Max(1, (filtered.Length + CorridorPageSize - 1) /
            CorridorPageSize);
        _corridorPage = Math.Clamp(_corridorPage, 1, pageCount);
        CorridorFindingsList.ItemsSource = filtered
            .Skip((_corridorPage - 1) * CorridorPageSize)
            .Take(CorridorPageSize)
            .ToArray();
        CorridorPageText.Text = filtered.Length == 0
            ? "0 findings"
            : $"Page {_corridorPage:N0} of {pageCount:N0} · {filtered.Length:N0} findings";
        RefreshButtons();
    }

    private int CorridorPageCount()
    {
        string search = CorridorSearchInput?.Text.Trim() ?? string.Empty;
        int count = _corridorFindings.Count(item =>
            search.Length == 0 || Matches(item.Finding, search));
        return Math.Max(1, (count + CorridorPageSize - 1) / CorridorPageSize);
    }

    private static bool Matches(CorridorFinding finding, string search) =>
        finding.Id.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        finding.Risk.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        finding.PairName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        finding.AggressorNet.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        finding.Layer.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        finding.ObjectType.Contains(search, StringComparison.OrdinalIgnoreCase);

    private void RefreshButtons()
    {
        if (CaptureButton is null)
        {
            return;
        }
        bool idle = _operation is null;
        CaptureButton.IsEnabled = idle && _bridge?.State.IsReady == true;
        ReplayButton.IsEnabled = idle && _workflow?.HasCapture == true;
        CorridorButton.IsEnabled = idle && _bridge?.State.IsReady == true &&
            _workflow?.HasCapture == true;
        NavigateFindingButton.IsEnabled = idle &&
            CorridorFindingsList.SelectedItem is ScalableCorridorFinding &&
            _corridorDocument is not null &&
            _bridge?.EngineSession.State.Document == _corridorDocument;
        PreviousCorridorPageButton.IsEnabled = idle && _corridorPage > 1;
        NextCorridorPageButton.IsEnabled = idle && _corridorPage < CorridorPageCount();
        CancelButton.IsEnabled = !idle;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _operation?.Cancel();
        if (_workflow is not null)
        {
            await _workflow.DisposeAsync();
        }
        _workflow = null;
        _bridge = null;
    }
}
