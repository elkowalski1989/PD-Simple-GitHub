using PcbEngine.Advanced;
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
        (int Id, PhysicalSymbolOperation Operation, string Title)[] titles =
        [
            (0, PhysicalSymbolOperation.Generate, "Generate symbol"),
            (1, PhysicalSymbolOperation.Padstack, "Define padstack"),
            (2, PhysicalSymbolOperation.PadReplace, "Replace pin padstack"),
            (3, PhysicalSymbolOperation.PinArray, "Place pin array"),
            (4, PhysicalSymbolOperation.PinPlace, "Place pin"),
            (5, PhysicalSymbolOperation.Align, "Align pins"),
            (6, PhysicalSymbolOperation.PinOne, "Mark pin one"),
            (7, PhysicalSymbolOperation.Renumber, "Renumber pins"),
            (8, PhysicalSymbolOperation.AssemblyOutline, "Draw assembly outline"),
            (9, PhysicalSymbolOperation.PlaceBound, "Draw place bound"),
            (10, PhysicalSymbolOperation.Height, "Set height"),
            (11, PhysicalSymbolOperation.Refdes, "Place refdes"),
            (12, PhysicalSymbolOperation.Fiducial, "Place fiducial"),
            (13, PhysicalSymbolOperation.Keepout, "Add keepout"),
            (14, PhysicalSymbolOperation.HoleSlot, "Add hole or slot"),
            (15, PhysicalSymbolOperation.ShapeToPad, "Convert shape to pad"),
            (16, PhysicalSymbolOperation.BgaStandardize, "Standardize BGA"),
            (17, PhysicalSymbolOperation.Describe, "Apply description"),
        ];
        Require(titles.Length == Enum.GetValues<PhysicalSymbolOperation>().Length,
            "The facade pins fewer operations than Engine declares.");
        foreach ((int id, PhysicalSymbolOperation operation, string title) in titles)
        {
            Require((int)operation == id,
                $"Engine revalued {operation} to {(int)operation}; the facade pins {id}.");
            Require(PhysicalSymbolOperationLabels.TitleForOperationId(id) == title,
                $"The facade title for operation {id} changed.");
            Require(PhysicalSymbolTool.TitleFor(operation) == title,
                $"The tool title for {operation} changed.");
            Require(PhysicalSymbolTool.TitleFor((PhysicalSymbolOperation)id) == title,
                $"The tool title for operation id {id} depends on the member name.");
        }
        checks += 4;

        (int Id, PhysicalSymbolCapabilityGroup Group, string Label)[] groups =
        [
            (0, PhysicalSymbolCapabilityGroup.PhysicalSymbolGeneration, "Generation"),
            (1, PhysicalSymbolCapabilityGroup.PinOperations, "Pins"),
            (2, PhysicalSymbolCapabilityGroup.OutlinesAndBounds, "Outlines and bounds"),
            (3, PhysicalSymbolCapabilityGroup.Holes, "Holes"),
            (4, PhysicalSymbolCapabilityGroup.Keepouts, "Keepouts"),
            (5, PhysicalSymbolCapabilityGroup.PadReplacement, "Pad replacement"),
            (6, PhysicalSymbolCapabilityGroup.PadstackDefinitions, "Padstack definitions"),
        ];
        Require(groups.Length == Enum.GetValues<PhysicalSymbolCapabilityGroup>().Length,
            "The facade pins fewer groups than Engine declares.");
        foreach ((int id, PhysicalSymbolCapabilityGroup group, string label) in groups)
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
        Require(PhysicalSymbolTool.TitleFor((PhysicalSymbolOperation)99) == "Symbol operation 99",
            "The tool does not delegate unknown operations to the facade fallback.");
        Require(!PhysicalSymbolOperationLabels.TitleForOperationId(99).Equals(
                PhysicalSymbolOperationLabels.TitleForOperationId(0), StringComparison.Ordinal),
            "The fallback collides with a curated title.");
        checks += 5;

        Require(PhysicalSymbolOperationLabels.FacadeVersion == 1, "The facade version changed silently.");
        string[] actionIds = Enum.GetValues<PhysicalSymbolOperation>()
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
