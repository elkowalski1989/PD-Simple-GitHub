using System.Collections.Immutable;
using System.Windows.Controls;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.PcbTools;
using PD.Simple.Tools.Catalog;

namespace PD.Simple.Tools.PhysicalSymbols;

/// <summary>
/// Guided physical-symbol view. The coordinator supplies captured definitions
/// and the selected Engine workspace. The runner retains the explicit stage
/// copy and one-shot native operation; this view creates no session or Host.
/// The definition list binds stable typed <see cref="PhysicalSymbolCatalogRow"/>
/// values. Refreshes preserve the selection by definition name; a prior name
/// that is gone from the refreshed catalog clears the selection.
/// </summary>
public partial class PhysicalSymbolsView : UserControl
{
    private DesignScene? _scene;
    private EngineDefinitionCatalog? _catalog;
    private bool _isLiveConnected;
    private string? _stagedSymbolName;
    private EngineSymbolBindingRunner? _runner;
    private ImmutableArray<PhysicalSymbolDefinitionSummary> _definitions = [];
    private ImmutableArray<PhysicalSymbolCatalogRow> _rows = [];
    private bool _rebuildingList;

    public bool CanChangeSession => _runner?.CanChangeSession ?? true;
    public string? CloseBlockReason => _runner?.CloseBlockReason;
    public event EventHandler? WorkflowStateChanged;
    public event EventHandler? StagedSessionSelectionRequested;

    public PhysicalSymbolsView()
    {
        InitializeComponent();
        RebuildList(null);
        Render();
    }

    public void ShowScene(DesignScene? scene, bool isLiveConnected)
    {
        ImmutableArray<PhysicalSymbolDefinitionSummary> definitions =
            PhysicalSymbolTool.SummarizeDefinitions(scene);
        string? preserved = CatalogSelection.ResolvePreservedName(
            SelectedName,
            definitions.Select(definition => definition.Name));
        _scene = scene;
        _catalog = null;
        _isLiveConnected = isLiveConnected;
        _definitions = definitions;
        _rows = CatalogSelection.BuildRows(_definitions);
        RebuildList(preserved);
        Render();
    }

    public void ShowCatalog(EngineDefinitionCatalog catalog, bool isLiveConnected)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ImmutableArray<PhysicalSymbolDefinitionSummary> definitions = CatalogPublication.SymbolSummaries(catalog);
        string? preserved = CatalogSelection.ResolvePreservedName(
            SelectedName, definitions.Select(definition => definition.Name));
        _scene = null;
        _catalog = catalog;
        _isLiveConnected = isLiveConnected;
        _definitions = definitions;
        _rows = CatalogSelection.BuildRows(definitions);
        RebuildList(preserved);
        Render();
    }

    public void StageSymbol(string? symbolName)
    {
        _stagedSymbolName = string.IsNullOrWhiteSpace(symbolName) ? null : symbolName;
        Render();
    }

    public void AttachRunner(EngineSymbolBindingRunner? runner)
    {
        if (!CanChangeSession && !ReferenceEquals(_runner, runner))
        {
            throw new InvalidOperationException(CloseBlockReason);
        }
        if (_runner is not null)
        {
            _runner.StateChanged -= Runner_StateChanged;
        }
        _runner = runner;
        if (_runner is not null)
        {
            _runner.StateChanged += Runner_StateChanged;
        }
        Render();
    }

    private void Runner_StateChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => Runner_StateChanged(sender, e));
            return;
        }
        RenderBinding();
        WorkflowStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void IntentChanged(object sender, TextChangedEventArgs e)
    {
        _runner?.InvalidateIntent();
    }

    private void SelectStagedSession_Click(object sender, System.Windows.RoutedEventArgs e) =>
        StagedSessionSelectionRequested?.Invoke(this, EventArgs.Empty);

    private void ChooseSource_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Allegro drawing (*.dra)|*.dra",
            CheckFileExists = true,
            Multiselect = false,
            Title = "Select the existing DRA to copy; the source will remain unchanged",
        };
        if (picker.ShowDialog() == true)
        {
            SymSourceDraBox.Text = picker.FileName;
            if (string.IsNullOrWhiteSpace(SymSymbolNameBox.Text))
            {
                SymSymbolNameBox.Text = System.IO.Path.GetFileNameWithoutExtension(picker.FileName);
            }
        }
    }

    private void StopWaiting_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        _runner?.StopWaiting();
        SymBindingResult.Text = "Stopped the local wait request. Native abort is not established; retain and check the submitted operation.";
    }

    private async void CheckSubmitted_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_runner is null)
        {
            return;
        }
        try
        {
            EnginePhysicalSymbolReconciliation result = await _runner.CheckSubmittedAsync();
            SymBindingResult.Text = $"Original operation {result.NativeOperationId}: {result.Diagnostic}. " +
                $"Further reconciliation required={result.RequiresReconciliation}. No mutation was replayed.";
        }
        catch (Exception error)
        {
            SymBindingResult.Text = "Operation check failed; owner retained: " + error.Message;
        }
        finally
        {
            RenderBinding();
        }
    }

    internal EngineSymbolBindingRunner? Runner => _runner;

    private async void StageButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_runner is null)
        {
            SymBindingResult.Text = "No Engine binding runner is attached.";
            return;
        }
        try
        {
            PhysicalSymbolStageCopy copy = await _runner.StageExistingAsync(SymSourceDraBox.Text.Trim(),
                SymStagingRootBox.Text.Trim(), SymSymbolNameBox.Text.Trim(), "pd-simple");
            EngineSymbolWorkArea area = copy.Area;
            StageSymbol(area.SymbolName);
            SymBindingResult.Text = $"Copied '{area.SymbolName}' to {area.StagedDraPath}; source preserved. " +
                $"Source/staged SHA-256 {copy.SourceSha256}, {copy.Bytes} bytes. " +
                "Open this file through Allegro's normal operation, then select its actual session below. Copying does not prove PACKAGE document kind.";
        }
        catch (Exception exception)
        {
            SymBindingResult.Text = "Staging failed: " + exception.Message;
        }
        finally
        {
            RenderBinding();
        }
    }

    private async void ActivateButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_runner is null)
        {
            SymBindingResult.Text = "No Engine binding runner is attached.";
            return;
        }
        try
        {
            await _runner.ActivatePackagedAsync().ConfigureAwait(true);
            SymBindingResult.Text = "The exact embedded Engine module matched its installed catalog. " +
                "PACKAGE document inspection and production qualification remain separate checks.";
        }
        catch (Exception exception)
        {
            SymBindingResult.Text = "Activation failed: " + exception.Message;
        }
        finally
        {
            RenderBinding();
        }
    }

    private bool TryBuildRequest(out EnginePhysicalSymbolRequest? request, out string error)
    {
        request = null;
        error = string.Empty;
        if (_runner is null)
        {
            error = "No Engine binding runner is attached.";
            return false;
        }
        if (_runner.StageCopy is null)
        {
            error = "Copy the selected existing DRA into a new stage first. A plan is not a file.";
            return false;
        }
        string selectedSource;
        string selectedRoot;
        try
        {
            selectedSource = System.IO.Path.GetFullPath(SymSourceDraBox.Text.Trim());
            selectedRoot = System.IO.Path.GetFullPath(SymStagingRootBox.Text.Trim());
        }
        catch (ArgumentException exception)
        {
            error = "Select valid source and stage paths: " + exception.Message;
            return false;
        }
        if (!string.Equals(_runner.StageCopy.Area.SymbolName, SymSymbolNameBox.Text.Trim(), StringComparison.Ordinal) ||
            !string.Equals(_runner.StageCopy.SourcePath, selectedSource, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(System.IO.Path.GetDirectoryName(_runner.StageCopy.Area.StagingRoot), selectedRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            error = "The stage inputs changed. Copy the displayed source and name before preparing.";
            return false;
        }
        if (!Enum.TryParse<EnginePhysicalSymbolOperation>(SymOperationBox.Text.Trim(), ignoreCase: true, out EnginePhysicalSymbolOperation operation))
        {
            error = "Choose one of the candidate operations.";
            return false;
        }
        string targetText = SymTargetBox.Text.Trim();
        int separator = targetText.IndexOf(':');
        if (separator <= 0 ||
            !Enum.TryParse<EnginePhysicalSymbolTargetKind>(targetText.Substring(0, separator).Trim(), ignoreCase: true, out EnginePhysicalSymbolTargetKind targetKind) ||
            string.IsNullOrWhiteSpace(targetText.Substring(separator + 1)))
        {
            error = "Enter the target as Kind:Identity, e.g. PadstackDefinition:PAD_A.";
            return false;
        }
        EnginePhysicalSymbolIntent intent;
        try
        {
            intent = EnginePhysicalSymbolIntent.FromJson(
                SymIntentBox.Text, SymContextFingerprintBox.Text.Trim());
        }
        catch (Exception exception)
        {
            error = "The intent envelope was rejected: " + exception.Message;
            return false;
        }
        try
        {
            request = _runner.BuildRequest(
                operation,
                new(targetKind, targetText.Substring(separator + 1).Trim()),
                new(ExpectedBeforeFingerprint: null, ExpectedTargetExists: null),
                EnginePhysicalSymbolPersistencePlan.SaveStagedDocument(_runner.StageCopy.Area.StagedDraPath),
                EnginePhysicalSymbolReadbackExpectation.AnyChange,
                intent);
            return true;
        }
        catch (Exception exception)
        {
            error = "The request was rejected: " + exception.Message;
            return false;
        }
    }

    private async void PrepareButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!TryBuildRequest(out EnginePhysicalSymbolRequest? request, out string error) || request is null)
        {
            SymBindingResult.Text = error;
            return;
        }
        try
        {
            EnginePhysicalSymbolPreparation preparation = await _runner!.PrepareAsync(request).ConfigureAwait(true);
            SymBindingResult.Text = $"Frozen preview: {preparation.Request.Operation} on {preparation.Request.Target.Kind}:" +
                $"{preparation.Request.Target.Identity}; {preparation.AffectedCount} affected; freshness {preparation.Freshness}. " +
                $"Plan {preparation.Plan.PlanHash}. " +
                (preparation.ProductionAssessment.Eligible
                    ? $"Production scope {preparation.ProductionAssessment.ScopeId} is eligible. Apply uses this exact preparation once."
                    : "Production apply is unavailable: " + string.Join(" ", preparation.ProductionAssessment.Diagnostics.Select(item => item.Message)));
        }
        catch (Exception exception)
        {
            SymBindingResult.Text = "Preview failed: " + exception.Message;
        }
        finally
        {
            RenderBinding();
        }
    }

    private async void ApplyButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_runner is null)
        {
            SymBindingResult.Text = "No Engine binding runner is attached.";
            return;
        }
        try
        {
            EnginePhysicalSymbolApplyEvidence evidence =
                await _runner.ApplyAsync(SymApprovalBox.Text.Trim()).ConfigureAwait(true);
            SymBindingResult.Text = EngineSymbolBindingRunner.DescribeApply(evidence);
        }
        catch (Exception exception)
        {
            SymBindingResult.Text = "Apply failed: " + exception.Message;
        }
        finally
        {
            RenderBinding();
        }
    }

    private async void PublishButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_runner is null)
        {
            SymBindingResult.Text = "No Engine binding runner is attached.";
            return;
        }
        if (_runner.StagedArea is null)
        {
            SymBindingResult.Text = "Stage a PACKAGE work area first.";
            return;
        }
        try
        {
            var plan = new EnginePhysicalSymbolPublicationPlan(
                _runner.StagedArea.StagedDraPath,
                SymDestinationBox.Text.Trim(),
                _runner.StagedArea.SymbolName + ".dra",
                EngineLibraryOverwritePolicy.FailIfExists,
                WriteInventoryManifest: true,
                CompilePsm: false);
            EnginePhysicalSymbolPublicationEvidence evidence = await _runner
                .PublishDraAsync(plan, SymApprovalBox.Text.Trim()).ConfigureAwait(true);
            SymBindingResult.Text = $"Library publication {evidence.Status}: {evidence.Artifacts.Count} committed artifact(s); " +
                $"reopen verified={evidence.ReopenVerified}; inventory={evidence.InventoryManifestPath ?? "unavailable"}. " +
                string.Join(" ", evidence.Diagnostics.Select(item => item.Message));
        }
        catch (Exception exception)
        {
            SymBindingResult.Text = "Publication failed: " + exception.Message;
        }
        finally
        {
            RenderBinding();
        }
    }

    private void RenderBinding()
    {
        if (SymBindingState is null)
        {
            return;
        }
        SymBindingState.Text = _runner?.StateText ?? "No Engine binding runner is attached.";
        EnginePhysicalSymbolExtensionDescriptor package = EngineSymbolBindingRunner.PackagedDescriptor;
        SymPackagedDescriptor.Text = $"Engine package module {package.ExtensionId} {package.Version}; " +
            $"{package.EntryFile}; SHA-256 {package.ContentHash}. This identity is read-only.";
        SymQualification.Text = _runner is null ? "No installed binding." : string.Join("\n",
            _runner.Capabilities.Select(item => $"{PhysicalSymbolTool.TitleFor(item.Operation)}: " +
                $"implementation={item.ImplementationPresent}; installed={item.InstalledAndMatched?.ToString() ?? "Unknown"}; " +
                $"acceptance={item.EnabledForAcceptance}; production scope={item.EnabledForProduction}. {item.UnavailableReason} " +
                string.Join(" ", item.AcceptedScope)));
        bool idle = _runner is { IsBusy: false, HasUnresolvedOutcome: false };
        SymStageButton.IsEnabled = idle;
        SymSelectSessionButton.IsEnabled = idle && _runner?.StageCopy is not null;
        SymActivateButton.IsEnabled = idle && _runner?.StageCopy is not null && _isLiveConnected;
        SymPrepareButton.IsEnabled = idle && _runner?.Binding?.IsCurrent == true;
        SymApplyButton.IsEnabled = _runner?.CanApply == true;
        SymPublishButton.IsEnabled = _runner?.CanPublish == true;
        SymStopWaitingButton.IsEnabled = _runner?.CanStopWaiting == true;
        SymCheckSubmittedButton.IsEnabled = _runner is { IsBusy: false, HasUnresolvedOutcome: true };
    }

    public void SelectDefinition(string? name)
    {
        SymDefinitionList.SelectedItem = CatalogSelection.FindRow(_rows, name);
    }

    internal ImmutableArray<PhysicalSymbolDefinitionSummary> Definitions => _definitions;

    internal string? StagedSymbolName => _stagedSymbolName;

    internal ImmutableArray<PhysicalSymbolToolAvailability> Actions =>
        PhysicalSymbolTool.DescribeActions(_scene, _isLiveConnected, _stagedSymbolName, _catalog);

    private string? SelectedName => SelectedRow?.Name;

    private PhysicalSymbolCatalogRow? SelectedRow => SymDefinitionList.SelectedItem as PhysicalSymbolCatalogRow;

    private void DefinitionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rebuildingList)
        {
            return;
        }
        Render();
    }

    private void RebuildList(string? nameToSelect)
    {
        _rebuildingList = true;
        try
        {
            SymDefinitionList.ItemsSource = _rows;
            SelectDefinition(nameToSelect);
        }
        finally
        {
            _rebuildingList = false;
        }
    }

    private void Render()
    {
        if (SymModeLine is null)
        {
            return;
        }
        RenderBinding();
        SymModeLine.Text = _scene is null && _catalog is null
            ? "Offline: select an existing DRA to create a stage. Load a capture to inspect definitions. Native editing requires the staged document's selected session and a qualified operation."
            : _isLiveConnected
                ? "Live session connected. Captured definitions remain available; the selected staged document and each operation's qualification are checked separately."
                : "Capture loaded, session disconnected. Definition inspection and local stage copying are available.";
        SymStageDetail.Text = _runner?.StageCopy is { } stage
            ? $"Staged DRA file: {stage.Area.StagedDraPath}. Open it normally and explicitly select its session; Preview verifies native PACKAGE kind."
            : _stagedSymbolName is null
                ? "No staged file. Select an existing DRA to copy without overwriting the company/library source."
                : $"Stage name selected: {_stagedSymbolName}. This label proves neither a copied file nor an opened native document.";
        PhysicalSymbolCatalogRow? selected = SelectedRow;
        SymDefinitionDetail.Text = selected is null
            ? "Select a definition to inspect its pins and padstacks."
            : selected.Detail;
        SymActionList.ItemsSource = Actions;
        SymLimitsDetail.Text = string.Join("\n", PhysicalSymbolTool.VendorLimits.Select(item =>
            $"{item.Operation}: {item.DiagnosticCode}: {item.Limitation}"));
    }
}
