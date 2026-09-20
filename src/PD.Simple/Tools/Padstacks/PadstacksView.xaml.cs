using System.Collections.Immutable;
using System.Text;
using System.Windows.Controls;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.PcbTools;

namespace PD.Simple.Tools.Padstacks;

/// <summary>
/// T11 padstack task view. The coordinator supplies one capture (or none when
/// disconnected) through <see cref="ShowScene"/>; this view owns no session,
/// creates no Host, and starts no native operation.
/// </summary>
public partial class PadstacksView : UserControl
{
    private DesignScene? _scene;
    private bool _isLiveConnected;
    private ImmutableArray<PadstackDefinitionSummary> _definitions = [];

    public PadstacksView()
    {
        InitializeComponent();
        Render();
    }

    public void ShowScene(DesignScene? scene, bool isLiveConnected)
    {
        _scene = scene;
        _isLiveConnected = isLiveConnected;
        _definitions = PadstackTool.SummarizeDefinitions(scene);
        Render();
    }

    public void SelectDefinition(string? name)
    {
        PadDefinitionList.SelectedItem = _definitions.FirstOrDefault(item =>
            string.Equals(item.Name, name, StringComparison.Ordinal));
    }

    internal ImmutableArray<PadstackDefinitionSummary> Definitions => _definitions;

    internal ImmutableArray<PadstackToolAvailability> Actions =>
        PadstackTool.DescribeActions(_scene, _isLiveConnected, SelectedName);

    private string? SelectedName => (PadDefinitionList.SelectedItem as PadstackDefinitionSummary)?.Name;

    private void DefinitionList_SelectionChanged(object sender, SelectionChangedEventArgs e) => Render();

    private void Render()
    {
        if (PadModeLine is null)
        {
            return;
        }
        PadModeLine.Text = _scene is null
            ? "Offline: no capture loaded. Inspection needs a capture; mutating actions need their qualified native owners."
            : _isLiveConnected
                ? "Live session connected. Offline inspection runs on the current capture; mutations stay behind their native gates."
                : "Capture loaded, session disconnected. Offline inspection available; live actions report their setup requirement.";
        PadDefinitionList.ItemsSource = _definitions.Select(item =>
            $"{item.Name}  ·  drill {(item.DrillMils?.ToString() ?? "unknown")}  ·  layers {item.LayerCount}  ·  use {item.TotalUse}").ToList();
        PadDefinitionDetail.Text = SelectedName is null
            ? "Select a definition to inspect its layers and usage."
            : DetailText(_definitions.First(item => string.Equals(item.Name, SelectedName, StringComparison.Ordinal)));
        PadUsageDetail.Text = UsageText();
        PadPlanDetail.Text = PlanText();
        PadActionList.ItemsSource = Actions;
        PadLimitsDetail.Text = string.Join("\n", PadstackTool.UnsupportedOperations.Select(item =>
            $"{item.Operation}: {item.DiagnosticCode}: {item.Limitation}"));
    }

    private string DetailText(PadstackDefinitionSummary summary)
    {
        var text = new StringBuilder();
        text.Append($"Definition {summary.Name}: drill ");
        text.Append(summary.DrillMils?.ToString() ?? "unknown");
        text.Append(" mils, plating ");
        text.Append(summary.Plated is null ? "unknown" : summary.Plated.Value ? "plated" : "non-plated");
        text.Append($", {summary.LayerCount} captured layer pad(s). ");
        text.Append("A definition default pad is not proof of resolved instance copper.");
        return text.ToString();
    }

    private string UsageText()
    {
        if (_scene is null)
        {
            return "No capture is loaded.";
        }
        if (SelectedName is null)
        {
            return "Select a definition to list its fresh usage references.";
        }
        PadstackDefinitionSummary summary = _definitions.First(item =>
            string.Equals(item.Name, SelectedName, StringComparison.Ordinal));
        return $"Where-used for {summary.Name} in this capture: " +
            $"{summary.ViaCount} board via(s), {summary.PinCount} board pin(s), " +
            $"{summary.SymbolPinCount} symbol-definition pin(s). " +
            "Revalidate from a fresh capture before any destructive step.";
    }

    private string PlanText()
    {
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
