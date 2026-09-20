using System.Collections.Immutable;
using System.Text;
using System.Windows.Controls;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.PcbTools;

namespace PD.Simple.Tools.PhysicalSymbols;

/// <summary>
/// T10 physical-symbol task view. The coordinator supplies one capture (or
/// none when disconnected) through <see cref="ShowScene"/> and the staged
/// PACKAGE symbol name through <see cref="StageSymbol"/>; this view owns no
/// session, creates no Host, stages no file, and starts no native operation.
/// </summary>
public partial class PhysicalSymbolsView : UserControl
{
    private DesignScene? _scene;
    private bool _isLiveConnected;
    private string? _stagedSymbolName;
    private ImmutableArray<PhysicalSymbolDefinitionSummary> _definitions = [];

    public PhysicalSymbolsView()
    {
        InitializeComponent();
        Render();
    }

    public void ShowScene(DesignScene? scene, bool isLiveConnected)
    {
        _scene = scene;
        _isLiveConnected = isLiveConnected;
        _definitions = PhysicalSymbolTool.SummarizeDefinitions(scene);
        Render();
    }

    public void StageSymbol(string? symbolName)
    {
        _stagedSymbolName = string.IsNullOrWhiteSpace(symbolName) ? null : symbolName;
        Render();
    }

    public void SelectDefinition(string? name)
    {
        SymDefinitionList.SelectedItem = _definitions.FirstOrDefault(item =>
            string.Equals(item.Name, name, StringComparison.Ordinal));
    }

    internal ImmutableArray<PhysicalSymbolDefinitionSummary> Definitions => _definitions;

    internal string? StagedSymbolName => _stagedSymbolName;

    internal ImmutableArray<PhysicalSymbolToolAvailability> Actions =>
        PhysicalSymbolTool.DescribeActions(_scene, _isLiveConnected, _stagedSymbolName);

    private string? SelectedName => (SymDefinitionList.SelectedItem as PhysicalSymbolDefinitionSummary)?.Name;

    private void DefinitionList_SelectionChanged(object sender, SelectionChangedEventArgs e) => Render();

    private void Render()
    {
        if (SymModeLine is null)
        {
            return;
        }
        SymModeLine.Text = _scene is null
            ? "Offline: no capture loaded. Definition inspection needs a capture; staged symbol work needs the shared live session and the qualified lane E binding."
            : _isLiveConnected
                ? "Live session connected. Offline definition inspection runs on the current capture; the 17 candidate operations stay behind the lane E binding and native acceptance."
                : "Capture loaded, session disconnected. Offline definition inspection available; staged work and publication report their setup requirement.";
        SymStageDetail.Text = _stagedSymbolName is null
            ? "No staged PACKAGE symbol document. Staging opens a disposable symbol work area without switching or closing the application PCB; a board instance is not that document."
            : $"Staged symbol document: {_stagedSymbolName}. Preview is non-mutating; apply needs the exact accepted preview, native before-state, and approval identity.";
        SymDefinitionList.ItemsSource = _definitions.Select(item =>
            $"{item.Name}  ·  pins {item.PinCount}  ·  padstacks {string.Join(", ", item.Padstacks.DefaultIfEmpty("none"))}").ToList();
        SymDefinitionDetail.Text = SelectedName is null
            ? "Select a definition to inspect its pins and padstacks."
            : DetailText(_definitions.First(item => string.Equals(item.Name, SelectedName, StringComparison.Ordinal)));
        SymActionList.ItemsSource = Actions;
        SymLimitsDetail.Text = string.Join("\n", PhysicalSymbolTool.VendorLimits.Select(item =>
            $"{item.Operation}: {item.DiagnosticCode}: {item.Limitation}"));
    }

    private static string DetailText(PhysicalSymbolDefinitionSummary summary)
    {
        var text = new StringBuilder();
        text.Append($"Definition {summary.Name}: {summary.PinCount} captured pin(s); padstacks ");
        text.Append(summary.Padstacks.IsEmpty ? "unknown" : string.Join(", ", summary.Padstacks));
        text.Append(". Source verification against package dimensions and the pin manifest runs on the staged PACKAGE document, not this board capture.");
        return text.ToString();
    }
}
