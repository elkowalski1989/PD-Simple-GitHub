using System.Collections.Immutable;
using System.Text;
using System.Windows.Controls;
using CircuitHub.AllegroBridge.Engine.Live;
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
    private EngineSymbolBindingRunner? _runner;
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
        RenderBinding();
        SymModeLine.Text = _scene is null
            ? "Offline: no capture loaded. Definition inspection needs a capture; staged symbol work needs the shared live session and the qualified lane E binding."
            : _isLiveConnected
                ? "Live session connected. Offline definition inspection runs on the current capture; the 17 candidate operations stay behind the lane E binding and native acceptance."
                : "Capture loaded, session disconnected. Offline definition inspection available; staged work and publication report their setup requirement.";
        SymStageDetail.Text = _runner?.StagedArea is { } area
            ? $"Staged PACKAGE document: {area.StagedDraPath} (identity {area.StagingIdentity}). Preview is non-mutating; apply needs the exact accepted preview, native before-state, and approval identity."
            : _stagedSymbolName is null
                ? "No staged PACKAGE symbol document. Staging opens a disposable symbol work area without switching or closing the application PCB; a board instance is not that document."
                : $"Staged symbol document: {_stagedSymbolName}. Preview is non-mutating; apply needs the exact accepted preview, native before-state, and approval identity.";
        SymDefinitionList.ItemsSource = _definitions.ToList();
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
