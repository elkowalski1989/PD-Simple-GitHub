using System.Collections.Immutable;
using System.Windows.Controls;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.PcbTools;
using PD.Simple.Tools.Catalog;

namespace PD.Simple.Tools.Padstacks;

/// <summary>
/// T11 padstack task view. The coordinator supplies one capture (or none when
/// disconnected) through <see cref="ShowScene"/>; this view owns no session,
/// creates no Host, and starts no native operation.
/// The definition list binds stable typed <see cref="PadstackCatalogRow"/>
/// values. Refreshes preserve the selection by definition name; a prior name
/// that is gone from the refreshed catalog clears the selection.
/// </summary>
public partial class PadstacksView : UserControl
{
    private DesignScene? _scene;
    private EngineDefinitionCatalog? _catalog;
    private bool _isLiveConnected;
    private ImmutableArray<PadstackDefinitionSummary> _definitions = [];
    private ImmutableArray<PadstackCatalogRow> _rows = [];
    private bool _rebuildingList;

    public PadstacksView()
    {
        InitializeComponent();
        RebuildList(null);
        Render();
    }

    public void ShowScene(DesignScene? scene, bool isLiveConnected)
    {
        ImmutableArray<PadstackDefinitionSummary> definitions =
            PadstackTool.SummarizeDefinitions(scene);
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
        ImmutableArray<PadstackDefinitionSummary> definitions = CatalogPublication.PadstackSummaries(catalog);
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

    public void SelectDefinition(string? name)
    {
        PadDefinitionList.SelectedItem = CatalogSelection.FindRow(_rows, name);
    }

    internal ImmutableArray<PadstackDefinitionSummary> Definitions => _definitions;

    internal ImmutableArray<PadstackToolAvailability> Actions =>
        PadstackTool.DescribeActions(_scene, _isLiveConnected, SelectedName, _catalog);

    private string? SelectedName => SelectedRow?.Name;

    private PadstackCatalogRow? SelectedRow => PadDefinitionList.SelectedItem as PadstackCatalogRow;

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
            PadDefinitionList.ItemsSource = _rows;
            SelectDefinition(nameToSelect);
        }
        finally
        {
            _rebuildingList = false;
        }
    }

    private void Render()
    {
        if (PadModeLine is null)
        {
            return;
        }
        PadModeLine.Text = _scene is null && _catalog is null
            ? "Offline: no capture loaded. Inspection needs a capture; mutating actions need their qualified native owners."
            : _isLiveConnected
                ? "Live session connected. Offline inspection runs on the current capture; mutations stay behind their native gates."
                : "Capture loaded, session disconnected. Offline inspection available; live actions report their setup requirement.";
        PadstackCatalogRow? selected = SelectedRow;
        PadDefinitionDetail.Text = selected is null
            ? "Select a definition to inspect its layers and usage."
            : selected.Detail + (_catalog is null ? string.Empty :
                " Layer names and counts are captured; definition layer geometry is unavailable in this metadata read.");
        PadUsageDetail.Text = _scene is null && _catalog is null
            ? "No capture is loaded."
            : selected is null
                ? "Select a definition to list its fresh usage references."
                : selected.Usage;
        PadPlanDetail.Text = PlanText();
        PadActionList.ItemsSource = Actions;
        PadLimitsDetail.Text = string.Join("\n", PadstackTool.UnsupportedOperations.Select(item =>
            $"{item.Operation}: {item.DiagnosticCode}: {item.Limitation}"));
    }

    private string PlanText()
    {
        if (_catalog is not null)
        {
            return SelectedName is null
                ? "Select a definition to inspect its metadata and where-used counts."
                : $"Definition {SelectedName}. Usage capture {_catalog.CaptureToken}. " +
                    PadstackTool.DescribePlan(PadstackTool.PlanPurge(
                        EnginePadstackPurgeMode.AllUnused, _catalog.CaptureToken)) +
                    " Instance geometry and layer geometry require a separate inspection.";
        }
        if (_scene is null)
        {
            return "No capture is loaded. Plans validate against a fresh capture; planning never executes.";
        }
        if (SelectedName is null)
        {
            return "Select a definition to preview its Engine plans. Plans validate; dispatch waits on the licensed gate.";
        }
        try
        {
            string stamp = EnginePadstackInspection.Stamp(_scene);
            string purge = PadstackTool.DescribePlan(
                PadstackTool.PlanPurge(EnginePadstackPurgeMode.AllUnused, stamp));
            string targeted = PadstackTool.AssessTargetedDelete(definitionInUse: true).Limitation;
            string diagnosis = PadstackTool.ExportDiagnosis(_scene, SelectedName);
            string firstLine = diagnosis.Split('\n').FirstOrDefault(line => line.StartsWith("Definition:", StringComparison.Ordinal))
                ?? "Definition evidence unavailable.";
            return $"{firstLine} Usage stamp {stamp}. {purge} Targeted delete: {targeted}";
        }
        catch (Exception exception)
        {
            return "Engine planning unavailable for this selection: " + exception.Message;
        }
    }
}
