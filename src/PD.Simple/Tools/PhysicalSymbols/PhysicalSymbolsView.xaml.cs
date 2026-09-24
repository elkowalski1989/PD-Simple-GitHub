using System.Collections.Immutable;
using System.Windows.Controls;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.PcbTools;
using PD.Simple.Tools.Catalog;

namespace PD.Simple.Tools.PhysicalSymbols;

/// <summary>
/// T10 physical-symbol task view. The coordinator supplies one capture (or
/// none when disconnected) through <see cref="ShowScene"/> and the staged
/// PACKAGE symbol name through <see cref="StageSymbol"/>; this view owns no
/// session, creates no Host, stages no file, and starts no native operation.
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
        _runner = runner;
        Render();
    }

    internal EngineSymbolBindingRunner? Runner => _runner;

    private void StageButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_runner is null)
        {
            SymBindingResult.Text = "No Engine binding runner is attached.";
            return;
        }
        try
        {
            EngineSymbolWorkArea area = _runner.PlanStage(
                SymStagingRootBox.Text.Trim(), SymSymbolNameBox.Text.Trim(), "pd-simple");
            StageSymbol(area.SymbolName);
            SymBindingResult.Text = $"Staged '{area.SymbolName}' at {area.StagedDraPath} (identity {area.StagingIdentity}). " +
                "Open this staged drawing in Allegro before previewing; a board instance is not that document.";
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
            var descriptor = new EnginePhysicalSymbolExtensionDescriptor(
                EnginePhysicalSymbolExtensionDescriptor.AcceptedExtensionId,
                SymExtVersionBox.Text.Trim(),
                SymExtEntryBox.Text.Trim(),
                SymExtHashBox.Text.Trim().ToLowerInvariant());
            await _runner.ActivateAsync(descriptor).ConfigureAwait(true);
            SymBindingResult.Text = "Binding activated for the exact release-manifest descriptor. " +
                "Capability and license state stay visible through the engine.physical-symbols capability.";
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
        if (_runner.StagedArea is null)
        {
            error = "Stage a PACKAGE work area first.";
            return false;
        }
        if (!Enum.TryParse<EnginePhysicalSymbolOperation>(SymOperationBox.Text.Trim(), ignoreCase: true, out EnginePhysicalSymbolOperation operation))
        {
            error = "Choose one of the 17 candidate operations.";
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
                EnginePhysicalSymbolPersistencePlan.SaveStagedDocument(_runner.StagedArea.StagedDraPath),
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
            SymBindingResult.Text = $"Preview complete without mutating: {preparation.AffectedCount} affected, " +
                $"freshness {preparation.Freshness}. Apply executes once with before/after readback.";
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
            SymBindingResult.Text = $"Published {evidence.Artifacts.Count} artifact(s) from fingerprint {evidence.SourceFingerprint} " +
                $"by '{evidence.ApprovalIdentity}'{(evidence.InventoryManifestPath is null ? " (no manifest)." : $" (manifest {evidence.InventoryManifestPath}).")}";
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
            ? "Offline: no capture loaded. Definition inspection needs a capture; staged symbol work needs the shared live session and the qualified lane E binding."
            : _isLiveConnected
                ? "Live session connected. Offline definition inspection runs on the current capture; the 17 candidate operations stay behind the lane E binding and native acceptance."
                : "Capture loaded, session disconnected. Offline definition inspection available; staged work and publication report their setup requirement.";
        SymStageDetail.Text = _runner?.StagedArea is { } area
            ? $"Staged PACKAGE document: {area.StagedDraPath} (identity {area.StagingIdentity}). Preview is non-mutating; apply needs the exact accepted preview, native before-state, and approval identity."
            : _stagedSymbolName is null
                ? "No staged PACKAGE symbol document. Staging opens a disposable symbol work area without switching or closing the application PCB; a board instance is not that document."
                : $"Staged symbol document: {_stagedSymbolName}. Preview is non-mutating; apply needs the exact accepted preview, native before-state, and approval identity.";
        PhysicalSymbolCatalogRow? selected = SelectedRow;
        SymDefinitionDetail.Text = selected is null
            ? "Select a definition to inspect its pins and padstacks."
            : selected.Detail;
        SymActionList.ItemsSource = Actions;
        SymLimitsDetail.Text = string.Join("\n", PhysicalSymbolTool.VendorLimits.Select(item =>
            $"{item.Operation}: {item.DiagnosticCode}: {item.Limitation}"));
    }
}
