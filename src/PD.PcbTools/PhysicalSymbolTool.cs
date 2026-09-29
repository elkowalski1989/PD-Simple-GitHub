using System.Collections.Immutable;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;

namespace PD.PcbTools;

/// <summary>T10 physical-symbol action identifiers owned by lane E.</summary>
public static class PhysicalSymbolToolActions
{
    public const string StageWorkArea = "symbol.stage-work-area";
    public const string InspectDefinitions = "symbol.inspect-definitions";
    public const string VerifySource = "symbol.verify-source";
    public const string PublishDra = "symbol.publish-dra";

    public static string ForOperation(EnginePhysicalSymbolOperation operation) =>
        "symbol.op." + operation.ToString().ToLowerInvariant();
}

/// <summary>One action with its operation-level availability and next step.</summary>
public sealed record PhysicalSymbolToolAvailability(
    string ActionId,
    string Title,
    string Group,
    bool Available,
    string Reason,
    string NextStep);

/// <summary>One captured symbol definition with its pin manifest.</summary>
public sealed record PhysicalSymbolDefinitionSummary(
    string Name,
    int PinCount,
    ImmutableArray<string> Padstacks)
{
    public bool HasPins => PinCount > 0;
}

/// <summary>One disclosed vendor/native boundary with its exact diagnostic code.</summary>
public sealed record PhysicalSymbolVendorLimit(
    string Operation,
    string DiagnosticCode,
    string Limitation);

/// <summary>Lane E registration data for the coordinator-owned tool registry.</summary>
public sealed record PhysicalSymbolToolRegistration(
    string ToolId,
    string Title,
    string Category,
    bool OpensOffline,
    string OfflineMode);

/// <summary>
/// T10 physical-symbol task policy over the frozen Engine contracts. The page
/// opens disconnected: captured symbol-definition inspection runs offline on
/// the current board capture. Every other action carries its exact Engine
/// capability reason instead of a silent disabled button. This type owns no
/// session, creates no Host, stages no file, and manufactures no native
/// authority: preview/apply/readback and DRA publication stay behind the lane
/// E public Engine binding and the licensed native gate.
/// </summary>
public static class PhysicalSymbolTool
{
    public static PhysicalSymbolToolRegistration Registration { get; } = new(
        "tools.physical-symbols",
        "Physical symbols",
        "Physical",
        true,
        "Captured symbol-definition and pin manifests run offline on the current capture. " +
        "Copying an explicit existing DRA into a new stage also runs offline. Native PACKAGE inspection and mutation require " +
        "the selected staged document and exact packaged binding; production mutation remains qualification-gated. PSM compilation is unavailable.");

    /// <summary>
    /// Plain engineering labels for the task screen. Advanced inputs stay
    /// behind the per-operation qualified binding; only the current task's
    /// inputs are shown. Labels resolve through the versioned facade by
    /// Engine identity so renames flow without PD edits.
    /// </summary>
    public static string TitleFor(EnginePhysicalSymbolOperation operation) =>
        PhysicalSymbolOperationLabels.TitleForOperationId((int)operation);

    public static string GroupLabelFor(EnginePhysicalSymbolCapabilityGroup group) =>
        PhysicalSymbolOperationLabels.GroupLabelForGroupId((int)group);

    public static ImmutableArray<PhysicalSymbolDefinitionSummary> SummarizeDefinitions(DesignScene? scene)
    {
        if (scene is null)
        {
            return [];
        }
        return scene.Data.Symbols
            .OrderBy(symbol => symbol.Name, StringComparer.Ordinal)
            .Select(symbol => new PhysicalSymbolDefinitionSummary(
                symbol.Name,
                symbol.Pins.Count(),
                symbol.Pins
                    .Select(pin => pin.Padstack)
                    .Where(padstack => !string.IsNullOrWhiteSpace(padstack))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(padstack => padstack, StringComparer.Ordinal)
                    .ToImmutableArray()))
            .ToImmutableArray();
    }

    public static ImmutableArray<PhysicalSymbolToolAvailability> DescribeActions(
        DesignScene? scene, bool isLiveConnected, string? stagedSymbolName,
        EngineDefinitionCatalog? catalog = null)
    {
        bool hasScene = scene is not null || catalog is not null;
        bool hasDefinitions = scene?.Data.Symbols.Length > 0 || catalog?.Symbols.Length > 0;
        bool hasStage = !string.IsNullOrWhiteSpace(stagedSymbolName);
        string captureStep = "Capture the current board from Board Explorer, then reopen Physical symbols.";
        var actions = new List<PhysicalSymbolToolAvailability>
        {
            new(PhysicalSymbolToolActions.StageWorkArea, "Copy existing DRA into stage", "Staging",
                true,
                "An explicitly selected existing DRA is copied into a new owned directory; its source remains unchanged. No document is opened by copying.",
                "Select source, parent directory and symbol name. Open the staged copy normally in Allegro and explicitly select its actual session."),
            new(PhysicalSymbolToolActions.InspectDefinitions, "Inspect symbol definitions", "Staging",
                hasDefinitions,
                hasDefinitions
                    ? "Captured definition and pin manifests are read from the current capture."
                    : hasScene ? "The current capture carries no symbol definitions." : "No capture is loaded.",
                hasDefinitions
                    ? "Select a definition to review its pins and padstacks before staging."
                    : captureStep),
            new(PhysicalSymbolToolActions.VerifySource, "Verify source manifest", "Staging",
                false,
                hasStage
                    ? "Source verification runs against the staged PACKAGE document through the lane E binding at integration; native gate T10-03 is NOT_EXECUTED."
                    : "Board captures carry no PACKAGE symbol document; a board instance with a similar symbol name is not that document.",
                hasStage
                    ? "Supply the source package dimensions and pin manifest with the staged document."
                    : "Stage a PACKAGE work area first, then verify unique pin numbering, geometry, and text roles against the source data."),
        };
        foreach (EnginePhysicalSymbolOperation operation in Enum.GetValues<EnginePhysicalSymbolOperation>())
        {
            EnginePhysicalSymbolCapabilityDiagnostic diagnostic =
                EnginePhysicalSymbolCapabilities.For(operation);
            actions.Add(new(
                PhysicalSymbolToolActions.ForOperation(operation),
                TitleFor(operation),
                GroupLabelFor(diagnostic.Group),
                false,
                diagnostic.Limitation,
                diagnostic.Qualification == EngineNativeQualification.ProductionQualifiedForScope
                    ? "Copy an empty PACKAGE DRA into stage, select its live session, match the packaged module and prepare the declared Generate scope. Apply requires the exact preparation's positive production assessment."
                    : "Qualify through the lane E public Engine binding (exact extension identity, staged PACKAGE " +
                        "document, preview/apply with pin and geometry readback) in the licensed slot; " +
                        "acceptance gate T10-02 remains open for this operation."));
        }
        actions.Add(new(
            PhysicalSymbolToolActions.PublishDra, "Publish DRA to library", "Publication",
            false,
            "Publication requires a completed apply with independent after readback, an explicit destination " +
            "with overwrite policy, byte validation, and a bounded approval identity through the lane E " +
            "Engine publication contract at integration. PSM compilation stays refused: axlCompileSymbol can " +
            "silently overwrite and voids native handles (Cadence SPB_25.1 share\\pcb\\examples\\skill\\DOC\\FUNCS\\axlCompileSymbol.txt).",
            hasStage
                ? "Complete and verify the staged apply first; a preview or staged DRA alone never counts as published."
                : "Stage a PACKAGE work area and complete its apply first; a preview or staged DRA alone never counts as published."));
        return actions.ToImmutableArray();
    }

    public static ImmutableArray<PhysicalSymbolVendorLimit> VendorLimits =>
    [
        new("PSM compilation",
            "library_compile_contract_insufficient",
            "PSM compilation (axlCompileSymbol) can silently overwrite, voids all native dbid handles, and has no packaged native owner. DRA publication proceeds without PSM output."),
        new("Production support",
            "physical_symbol_native_acceptance_pending",
            "Generate has a bounded mils/accuracy-2 rectangular SMD, DRA-only scope enforced by its frozen preparation. Other operations and shapes remain acceptance candidates; PSM and PAD output are unavailable."),
        new("Board instances",
            "physical_symbol_staging_wrong_document",
            "A board instance with a similar symbol name is not the staged PACKAGE symbol document; requests naming another live document are rejected, never rebound."),
    ];
}
