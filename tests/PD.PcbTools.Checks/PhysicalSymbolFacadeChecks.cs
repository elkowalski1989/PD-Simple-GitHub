using CircuitHub.AllegroBridge.Engine.Live;
using PD.PcbTools;

/// <summary>
/// D facade checks. Operation and group labels resolve through the versioned
/// PD facade by Engine numeric identity: renames flow without PD edits,
/// unknown future identities render an explicit fallback, and any Engine
/// revalue fails the pinned identities loudly instead of mislabeling.
/// </summary>
internal static class PhysicalSymbolFacadeChecks
{
    internal static int Run()
    {
        int checks = 0;
        (int Id, EnginePhysicalSymbolOperation Operation, string Title)[] titles =
        [
            (0, EnginePhysicalSymbolOperation.Generate, "Generate symbol"),
            (1, EnginePhysicalSymbolOperation.Padstack, "Define padstack"),
            (2, EnginePhysicalSymbolOperation.PadReplace, "Replace pin padstack"),
            (3, EnginePhysicalSymbolOperation.PinArray, "Place pin array"),
            (4, EnginePhysicalSymbolOperation.PinPlace, "Place pin"),
            (5, EnginePhysicalSymbolOperation.Align, "Align pins"),
            (6, EnginePhysicalSymbolOperation.PinOne, "Mark pin one"),
            (7, EnginePhysicalSymbolOperation.Renumber, "Renumber pins"),
            (8, EnginePhysicalSymbolOperation.AssemblyOutline, "Draw assembly outline"),
            (9, EnginePhysicalSymbolOperation.PlaceBound, "Draw place bound"),
            (10, EnginePhysicalSymbolOperation.Height, "Set height"),
            (11, EnginePhysicalSymbolOperation.Refdes, "Place refdes"),
            (12, EnginePhysicalSymbolOperation.Fiducial, "Place fiducial"),
            (13, EnginePhysicalSymbolOperation.Keepout, "Add keepout"),
            (14, EnginePhysicalSymbolOperation.HoleSlot, "Add hole or slot"),
            (15, EnginePhysicalSymbolOperation.ShapeToPad, "Convert shape to pad"),
            (16, EnginePhysicalSymbolOperation.BgaStandardize, "Standardize BGA"),
        ];
        Require(titles.Length == Enum.GetValues<EnginePhysicalSymbolOperation>().Length,
            "The facade pins fewer operations than Engine declares.");
        foreach ((int id, EnginePhysicalSymbolOperation operation, string title) in titles)
        {
            Require((int)operation == id,
                $"Engine revalued {operation} to {(int)operation}; the facade pins {id}.");
            Require(PhysicalSymbolOperationLabels.TitleForOperationId(id) == title,
                $"The facade title for operation {id} changed.");
            Require(PhysicalSymbolTool.TitleFor(operation) == title,
                $"The tool title for {operation} changed.");
            Require(PhysicalSymbolTool.TitleFor((EnginePhysicalSymbolOperation)id) == title,
                $"The tool title for operation id {id} depends on the member name.");
        }
        checks += 4;

        (int Id, EnginePhysicalSymbolCapabilityGroup Group, string Label)[] groups =
        [
            (0, EnginePhysicalSymbolCapabilityGroup.PhysicalSymbolGeneration, "Generation"),
            (1, EnginePhysicalSymbolCapabilityGroup.PinOperations, "Pins"),
            (2, EnginePhysicalSymbolCapabilityGroup.OutlinesAndBounds, "Outlines and bounds"),
            (3, EnginePhysicalSymbolCapabilityGroup.Holes, "Holes"),
            (4, EnginePhysicalSymbolCapabilityGroup.Keepouts, "Keepouts"),
            (5, EnginePhysicalSymbolCapabilityGroup.PadReplacement, "Pad replacement"),
            (6, EnginePhysicalSymbolCapabilityGroup.PadstackDefinitions, "Padstack definitions"),
        ];
        Require(groups.Length == Enum.GetValues<EnginePhysicalSymbolCapabilityGroup>().Length,
            "The facade pins fewer groups than Engine declares.");
        foreach ((int id, EnginePhysicalSymbolCapabilityGroup group, string label) in groups)
        {
            Require((int)group == id,
                $"Engine revalued {group} to {(int)group}; the facade pins {id}.");
            Require(PhysicalSymbolOperationLabels.GroupLabelForGroupId(id) == label,
                $"The facade label for group {id} changed.");
            Require(PhysicalSymbolTool.GroupLabelFor(group) == label,
                $"The tool label for {group} changed.");
        }
        checks += 4;

        Require(PhysicalSymbolOperationLabels.TitleForOperationId(99) == "Symbol operation 99",
            "A future operation identity did not flow through the facade.");
        Require(PhysicalSymbolOperationLabels.TitleForOperationId(-1) == "Symbol operation -1",
            "A negative operation identity threw instead of flowing through.");
        Require(PhysicalSymbolOperationLabels.GroupLabelForGroupId(99) == "Group 99",
            "A future group identity did not flow through the facade.");
        Require(PhysicalSymbolTool.TitleFor((EnginePhysicalSymbolOperation)99) == "Symbol operation 99",
            "The tool does not delegate unknown operations to the facade fallback.");
        Require(!PhysicalSymbolOperationLabels.TitleForOperationId(99).Equals(
                PhysicalSymbolOperationLabels.TitleForOperationId(0), StringComparison.Ordinal),
            "The fallback collides with a curated title.");
        checks += 5;

        Require(PhysicalSymbolOperationLabels.FacadeVersion == 1, "The facade version changed silently.");
        string[] actionIds = Enum.GetValues<EnginePhysicalSymbolOperation>()
            .Select(PhysicalSymbolToolActions.ForOperation)
            .ToArray();
        Require(actionIds.All(id => id.StartsWith("symbol.op.", StringComparison.Ordinal)) &&
            actionIds.Distinct(StringComparer.Ordinal).Count() == actionIds.Length,
            "Operation action ids lost their registry shape or uniqueness.");
        checks += 2;
        return checks;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Physical symbol facade check failed: " + message);
        }
    }
}
