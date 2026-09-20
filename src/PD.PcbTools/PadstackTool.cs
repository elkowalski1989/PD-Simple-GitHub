using System.Collections.Immutable;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;

namespace PD.PcbTools;

/// <summary>T11 padstack action identifiers owned by lane F.</summary>
public static class PadstackToolActions
{
    public const string InspectDefinitions = "padstack.inspect-definitions";
    public const string InspectInstances = "padstack.inspect-instances";
    public const string Compare = "padstack.compare";
    public const string WhereUsed = "padstack.where-used";
    public const string CreateDefinition = "padstack.create-definition";
    public const string UpdateGlobalAttributes = "padstack.update-global-attributes";
    public const string ReplaceBoardVia = "padstack.replace-board-via";
    public const string ReplaceSymbolPin = "padstack.replace-symbol-pin";
    public const string PurgeUnused = "padstack.purge-unused";
    public const string WriteLibraryFile = "padstack.write-library-file";
    public const string RedefineByReplacement = "padstack.redefine-by-replacement";
}

/// <summary>One action with its operation-level availability and next step.</summary>
public sealed record PadstackToolAvailability(
    string ActionId,
    string Title,
    bool Available,
    string Reason,
    string NextStep);

/// <summary>One definition with its captured usage counts.</summary>
public sealed record PadstackDefinitionSummary(
    string Name,
    bool Found,
    decimal? DrillMils,
    bool? Plated,
    int LayerCount,
    int ViaCount,
    int PinCount,
    int SymbolPinCount)
{
    public int TotalUse => ViaCount + PinCount + SymbolPinCount;
}

/// <summary>Lane F registration data for the coordinator-owned tool registry.</summary>
public sealed record PadstackToolRegistration(
    string ToolId,
    string Title,
    string Category,
    bool OpensOffline,
    string OfflineMode);

/// <summary>
/// T11 padstack task policy over the frozen Engine contracts. Offline
/// inspection summaries run on any supplied capture; every mutating action
/// carries its exact capability reason instead of a silent disabled button.
/// This type owns no session, creates no Host, and manufactures no native
/// authority: live execution stays behind the licensed lane F gate.
/// </summary>
public static class PadstackTool
{
    public static PadstackToolRegistration Registration { get; } = new(
        "tools.padstacks",
        "Padstacks",
        "Physical",
        true,
        "Definition/instance inspection, comparison, and where-used run offline on the current capture. " +
        "Creation, replacement, purge, and library output require their qualified native owners.");

    public static ImmutableArray<PadstackDefinitionSummary> SummarizeDefinitions(DesignScene? scene)
    {
        if (scene is null)
        {
            return [];
        }
        return scene.Data.Padstacks
            .OrderBy(definition => definition.Name, StringComparer.Ordinal)
            .Select(definition => new PadstackDefinitionSummary(
                definition.Name,
                true,
                definition.DrillDiameter?.Mils,
                definition.Plated,
                definition.Layers.Length,
                scene.Data.Copper.Count(copper =>
                    copper.Via is { } via &&
                    string.Equals(via.Padstack, definition.Name, StringComparison.Ordinal)),
                scene.Data.Copper.Count(copper =>
                    copper.Pin is { } pin &&
                    string.Equals(pin.Padstack, definition.Name, StringComparison.Ordinal)) +
                scene.Data.Pins.Count(pin =>
                    string.Equals(pin.Padstack, definition.Name, StringComparison.Ordinal)),
                scene.Data.Symbols
                    .SelectMany(symbol => symbol.Pins)
                    .Count(pin => string.Equals(pin.Padstack, definition.Name, StringComparison.Ordinal))))
            .ToImmutableArray();
    }

    public static ImmutableArray<PadstackToolAvailability> DescribeActions(
        DesignScene? scene, bool isLiveConnected, string? selectedDefinition)
    {
        bool hasScene = scene is not null;
        bool hasSelection = hasScene && !string.IsNullOrWhiteSpace(selectedDefinition) &&
            scene!.Data.Padstacks.Any(definition =>
                string.Equals(definition.Name, selectedDefinition, StringComparison.Ordinal));
        string captureStep = "Capture the current board from Board Explorer, then reopen Padstacks.";
        var actions = new List<PadstackToolAvailability>
        {
            new(PadstackToolActions.InspectDefinitions, "Inspect definitions",
                hasScene,
                hasScene ? "Definition list is read from the current capture." : "No capture is loaded.",
                hasScene ? "Select a definition to inspect its layers and usage." : captureStep),
            new(PadstackToolActions.InspectInstances, "Inspect instances",
                hasSelection,
                hasSelection ? "Board and symbol instances are read from the current capture."
                    : selectedDefinition is null ? "Select a definition first." : "No capture is loaded, or the selection left the capture.",
                hasSelection ? "Review span, orientation, and backdrill evidence per instance." : captureStep),
            new(PadstackToolActions.Compare, "Compare definitions",
                hasSelection,
                hasSelection ? "Side-by-side qualified diff of two captured definitions."
                    : "Select a definition first.",
                hasSelection ? "Choose a second definition to diff against." : captureStep),
            new(PadstackToolActions.WhereUsed, "Where-used",
                hasSelection,
                hasSelection ? "Fresh usage references from the current capture identity."
                    : "Select a definition first.",
                hasSelection ? "Revalidate from a fresh capture before any destructive step." : captureStep),
            new(PadstackToolActions.CreateDefinition, "Create definition",
                false,
                EnginePhysicalSymbolCapabilities.For(EnginePadstackWorkflowOperation.CreateDefinition).Limitation,
                "Wait for the qualified lane E binding; native gate T11-03 is NOT_EXECUTED."),
            new(PadstackToolActions.UpdateGlobalAttributes, "Update global attributes",
                false,
                EnginePhysicalSymbolCapabilities.For(EnginePadstackWorkflowOperation.UpdateGlobalAttributes).Limitation,
                "Lane F ships the managed contract and Skill/pcb_padstacks.il owner; live dispatch is wired at integration and qualified in the licensed slot."),
            new(PadstackToolActions.ReplaceBoardVia, "Replace standalone board via",
                hasScene && isLiveConnected,
                hasScene && isLiveConnected
                    ? "Guarded single-via replacement through the qualified NativeEdits path."
                    : !hasScene ? "No capture is loaded."
                    : "The shared live session is not connected; replacement never runs offline.",
                hasScene && isLiveConnected
                    ? "Pick a standalone target and a same-capture template in NativeEdits."
                    : captureStep),
            new(PadstackToolActions.ReplaceSymbolPin, "Replace symbol-definition pin",
                false,
                EnginePhysicalSymbolCapabilities.For(EnginePadstackWorkflowOperation.ReplaceSymbolDefinitionPin).Limitation,
                "Wait for the qualified lane E binding; native gate T11-04 is NOT_EXECUTED."),
            new(PadstackToolActions.PurgeUnused, "Purge unused definitions",
                false,
                EnginePhysicalSymbolCapabilities.For(EnginePadstackWorkflowOperation.PurgeUnusedDefinitions).Limitation,
                "Lane F ships the managed contract and Skill/pcb_padstacks.il owner; global purge is never targeted delete."),
            new(PadstackToolActions.WriteLibraryFile, "Write PAD library file",
                false,
                EnginePhysicalSymbolCapabilities.For(EnginePadstackWorkflowOperation.WriteLibraryFile).Limitation,
                "Lane F ships the offline staging/publication contract; the native write waits on the licensed slot."),
            new(PadstackToolActions.RedefineByReplacement, "Redefine by replacement",
                hasScene && isLiveConnected,
                hasScene && isLiveConnected
                    ? "Honest three-step alternative to unsupported in-place layer editing."
                    : !hasScene ? "No capture is loaded." : "The shared live session is not connected.",
                hasScene && isLiveConnected
                    ? "Create the replacement, swap each instance, then purge only freshly revalidated unused state."
                    : captureStep),
        };
        return actions.ToImmutableArray();
    }

    public static ImmutableArray<(string Operation, string DiagnosticCode, string Limitation)> UnsupportedOperations =>
    [
        (nameof(EnginePadstackWorkflowOperation.UpdateLayerGeometry),
            EnginePhysicalSymbolCapabilities.For(EnginePadstackWorkflowOperation.UpdateLayerGeometry).DiagnosticCode,
            EnginePhysicalSymbolCapabilities.For(EnginePadstackWorkflowOperation.UpdateLayerGeometry).Limitation),
        (nameof(EnginePadstackWorkflowOperation.DeleteDefinition),
            EnginePhysicalSymbolCapabilities.For(EnginePadstackWorkflowOperation.DeleteDefinition).DiagnosticCode,
            EnginePhysicalSymbolCapabilities.For(EnginePadstackWorkflowOperation.DeleteDefinition).Limitation),
    ];
}
