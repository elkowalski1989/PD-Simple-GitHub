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
/// T11 padstack task policy over the staged 1.13.0-preview.94 Engine
/// contracts. Offline inspection summaries run on any supplied capture;
/// inspection and workflow planning below dispatch the Engine authorities
/// (EnginePadstackInspection, EnginePadstackWorkflows, EnginePadstackLibrary
/// validation); every mutating action carries its exact capability reason
/// instead of a silent disabled button. This type owns no session, creates
/// no Host, and manufactures no native authority: planning is validation,
/// never execution, and live dispatch stays behind the licensed lane F gate.
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
                "Plan through PadstackTool.PlanGlobalEdit (Engine-validated, names the pending native call); live dispatch waits on the licensed gate."),
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
                "Plan through PadstackTool.PlanPurge against a fresh usage stamp; global purge is never targeted delete."),
            new(PadstackToolActions.WriteLibraryFile, "Write PAD library file",
                false,
                EnginePhysicalSymbolCapabilities.For(EnginePadstackWorkflowOperation.WriteLibraryFile).Limitation,
                "Validate through PadstackTool.ValidateLibraryRequest; the native write waits on the licensed slot."),
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

    /// <summary>
    /// The eight-operation Engine catalog against actual native capability.
    /// A plan below is validation, never execution: native dispatch waits on
    /// the licensed gate.
    /// </summary>
    public static ImmutableArray<EnginePadstackCatalogRow> CatalogStatus() =>
        EnginePadstackWorkflows.CheckAllEight();

    /// <summary>
    /// Inspects one captured definition through the Engine inspection
    /// authority. An instance view is never presented as a definition.
    /// </summary>
    public static EnginePadstackDefinitionView InspectDefinition(DesignScene scene, string name)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return EnginePadstackInspection.InspectDefinition(scene, name);
    }

    /// <summary>
    /// Inspects captured board and symbol instances of one definition. Counts
    /// travel with the capture stamp; revalidate from a fresh capture before
    /// any destructive step.
    /// </summary>
    public static EnginePadstackUsage InspectInstances(DesignScene scene, string name)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return EnginePadstackInspection.InspectInstances(scene, name);
    }

    /// <summary>Compares two captured definitions side by side.</summary>
    public static EnginePadstackComparison CompareDefinitions(DesignScene scene, string left, string right)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentException.ThrowIfNullOrWhiteSpace(left);
        ArgumentException.ThrowIfNullOrWhiteSpace(right);
        return EnginePadstackInspection.Compare(scene, left, right);
    }

    /// <summary>
    /// Exports captured definition, usage, and optional comparison evidence
    /// as deterministic diagnostics. Captured evidence only, no native
    /// authority.
    /// </summary>
    public static string ExportDiagnosis(DesignScene scene, string name, string? compareWith = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        EnginePadstackDefinitionView definition = InspectDefinition(scene, name);
        EnginePadstackUsage usage = InspectInstances(scene, name);
        EnginePadstackComparison? comparison = string.IsNullOrWhiteSpace(compareWith)
            ? null
            : CompareDefinitions(scene, name, compareWith);
        return EnginePadstackInspection.ExportDiagnostics(definition, usage, comparison);
    }

    /// <summary>
    /// Dispatches the Engine global-edit planner for one shared definition.
    /// The plan names the exact pending native call; planning edits nothing
    /// and never stands in for dispatch. Layer pad geometry stays outside the
    /// API; the definition is verified in the supplied capture when present.
    /// </summary>
    public static EnginePadstackGlobalEditPlan PlanGlobalEdit(
        string definitionName,
        IEnumerable<EnginePadstackAttributeChange> changes,
        DesignScene? scene)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionName);
        ArgumentNullException.ThrowIfNull(changes);
        EnginePadstackDefinitionView? inspected = null;
        if (scene is not null)
        {
            EnginePadstackDefinitionView candidate = EnginePadstackInspection.InspectDefinition(scene, definitionName);
            inspected = candidate.Found ? candidate : null;
        }
        return EnginePadstackWorkflows.PlanGlobalEdit(definitionName, changes, inspected);
    }

    /// <summary>
    /// Dispatches the Engine purge planner. Global unused/derived purge only:
    /// the plan carries its own cautions and is never presented as targeted
    /// deletion. The usage stamp must come from a fresh capture.
    /// </summary>
    public static EnginePadstackPurgePlan PlanPurge(EnginePadstackPurgeMode mode, string usageStamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(usageStamp);
        return EnginePadstackWorkflows.PlanPurge(mode, usageStamp);
    }

    /// <summary>
    /// Targeted deletion is never available; in-use definitions are an
    /// explicit no-go and unused definitions go through global purge only.
    /// </summary>
    public static EnginePadstackDeletionAssessment AssessTargetedDelete(bool definitionInUse) =>
        EnginePadstackWorkflows.AssessDeletion(definitionInUse);

    /// <summary>
    /// Parses one native purge receipt into a purged count. A missing or
    /// invalid receipt is rejected, never treated as success.
    /// </summary>
    public static int ParsePurgeReceipt(int? purgedCount) =>
        EnginePadstackWorkflows.ParsePurgeReceipt(purgedCount);

    /// <summary>
    /// Dispatches the honest redefine-by-replacement planner: three distinct
    /// operations (create, swap each instance, conditional purge tail), never
    /// one in-place layer edit.
    /// </summary>
    public static EnginePadstackRedefinePlan PlanRedefinition(
        string existingName, string replacementName, int affectedInstanceCount, bool purgeTail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(existingName);
        ArgumentException.ThrowIfNullOrWhiteSpace(replacementName);
        return EnginePadstackWorkflows.PlanRedefinition(
            existingName, replacementName, affectedInstanceCount, purgeTail);
    }

    /// <summary>
    /// Validates a board-pin replacement request. Documented API, no packaged
    /// owner: the request stays explicitly unresolved.
    /// </summary>
    public static EnginePadstackPendingNativePlan RequestBoardPinReplacement(
        string componentRefdes, string pinNumber, string replacementPadstack) =>
        EnginePadstackWorkflows.RequestBoardPinReplacement(componentRefdes, pinNumber, replacementPadstack);

    /// <summary>
    /// Validates a symbol-definition pin replacement request against the lane
    /// E acceptance path with expected-current-padstack preconditions.
    /// </summary>
    public static EnginePadstackPendingNativePlan RequestSymbolPinReplacement(
        string symbolName, string pinNumber, string expectedCurrentPadstack, string replacementPadstack) =>
        EnginePadstackWorkflows.RequestSymbolPinReplacement(
            symbolName, pinNumber, expectedCurrentPadstack, replacementPadstack);

    /// <summary>
    /// Validates a create-definition request against the lane E acceptance
    /// path. Production native acceptance remains open.
    /// </summary>
    public static EnginePadstackPendingNativePlan RequestCreateDefinition(
        string definitionName, string? drillSummary, int layerPadCount) =>
        EnginePadstackWorkflows.RequestCreateDefinition(definitionName, drillSummary, layerPadCount);

    /// <summary>
    /// Validates one PAD library-output request against the offline
    /// staging/publication contract. The native write waits on the licensed
    /// gate and explicit destination policy.
    /// </summary>
    public static EnginePadstackLibraryRequest ValidateLibraryRequest(EnginePadstackLibraryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return EnginePadstackLibrary.ValidateRequest(request);
    }

    /// <summary>Renders the exact pending native write call for one definition.</summary>
    public static string LibraryWriteCall(string definitionName, string outputName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputName);
        return EnginePadstackLibrary.NativeWriteCall(definitionName, outputName);
    }

    /// <summary>
    /// One-line honesty summaries for plan display. Plans are validation, not
    /// execution; purge is global-only; replacement is three steps.
    /// </summary>
    public static string DescribePlan(EnginePadstackGlobalEditPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return $"Global edit '{plan.DefinitionName}': {plan.OrderedArguments.Length} argument(s), " +
            $"verified in capture: {plan.DefinitionVerifiedInCapture}, stales DRC: {plan.StalesDrc}. " +
            $"Pending native call {plan.NativeCall}. Planning only; dispatch waits on the licensed gate.";
    }

    public static string DescribePlan(EnginePadstackPurgePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return $"Purge {plan.Mode} (usage {plan.UsageStamp}): global unused purge only, never targeted delete. " +
            $"Pending native call {plan.NativeCall}. {string.Join(" ", plan.Cautions)}";
    }

    public static string DescribePlan(EnginePadstackRedefinePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return $"Redefine '{plan.ExistingName}' by replacement with '{plan.ReplacementName}' " +
            $"({plan.AffectedInstanceCount} instance(s), purge tail: {plan.PurgeTail}). " +
            string.Join(" ", plan.Steps);
    }
}
