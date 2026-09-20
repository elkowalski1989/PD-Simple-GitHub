using System.Collections.Immutable;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Geometry;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.PcbTools;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
    checks++;
}

Check(PadstackTool.Registration.ToolId == "tools.padstacks" &&
    PadstackTool.Registration.Category == "Physical" &&
    PadstackTool.Registration.OpensOffline,
    "Tool registration data is wrong.");

ImmutableArray<PadstackToolAvailability> disconnected =
    PadstackTool.DescribeActions(null, false, null);
Check(disconnected.Length == 11 && disconnected.All(action => !action.Available) &&
    disconnected.All(action => !string.IsNullOrWhiteSpace(action.Reason) &&
        !string.IsNullOrWhiteSpace(action.NextStep)),
    "Disconnected actions must all be unavailable with reason and next step.");

DesignScene scene = PadstackScene();
ImmutableArray<PadstackDefinitionSummary> summaries = PadstackTool.SummarizeDefinitions(scene);
Check(summaries.Length == 2 && summaries[0].Name == "VIA_A" && summaries[1].Name == "VIA_B",
    "Definition summaries are missing or unsorted.");
PadstackDefinitionSummary viaA = summaries[0];
Check(viaA.DrillMils == 10 && viaA.Plated == true && viaA.LayerCount == 2 &&
    viaA.ViaCount == 1 && viaA.PinCount == 1 && viaA.SymbolPinCount == 1,
    "Definition usage counts are wrong.");
Check(PadstackTool.SummarizeDefinitions(null).IsEmpty, "Null scene must summarize to empty.");

ImmutableArray<PadstackToolAvailability> offline =
    PadstackTool.DescribeActions(scene, false, "VIA_A");
Check(Find(offline, PadstackToolActions.InspectDefinitions).Available &&
    Find(offline, PadstackToolActions.InspectInstances).Available &&
    Find(offline, PadstackToolActions.Compare).Available &&
    Find(offline, PadstackToolActions.WhereUsed).Available,
    "Offline inspection actions must be available on a capture with selection.");
Check(!Find(offline, PadstackToolActions.ReplaceBoardVia).Available &&
    Find(offline, PadstackToolActions.ReplaceBoardVia).Reason.Contains("not connected", StringComparison.Ordinal),
    "Board replacement must require the live session.");
Check(!Find(offline, PadstackToolActions.CreateDefinition).Available &&
    !Find(offline, PadstackToolActions.ReplaceSymbolPin).Available &&
    Find(offline, PadstackToolActions.CreateDefinition).NextStep.Contains("lane E", StringComparison.Ordinal),
    "Lane E pending actions must stay unavailable with routing.");
Check(!Find(offline, PadstackToolActions.UpdateGlobalAttributes).Available &&
    !Find(offline, PadstackToolActions.PurgeUnused).Available &&
    !Find(offline, PadstackToolActions.WriteLibraryFile).Available,
    "Lane F native-gated actions must stay unavailable before the licensed slot.");

ImmutableArray<PadstackToolAvailability> live =
    PadstackTool.DescribeActions(scene, true, "VIA_A");
Check(Find(live, PadstackToolActions.ReplaceBoardVia).Available &&
    Find(live, PadstackToolActions.RedefineByReplacement).Available,
    "Live session must enable the guarded replacement workflows.");

ImmutableArray<PadstackToolAvailability> noSelection = PadstackTool.DescribeActions(scene, true, null);
Check(!Find(noSelection, PadstackToolActions.InspectInstances).Available &&
    !Find(noSelection, PadstackToolActions.Compare).Available &&
    !Find(noSelection, PadstackToolActions.WhereUsed).Available,
    "Instance actions must require a selected definition.");
ImmutableArray<PadstackToolAvailability> staleSelection =
    PadstackTool.DescribeActions(scene, true, "MISSING");
Check(!Find(staleSelection, PadstackToolActions.InspectInstances).Available,
    "A stale selection must not enable instance actions.");

var unsupported = PadstackTool.UnsupportedOperations;
Check(unsupported.Length == 2 &&
    unsupported.Any(item => item.DiagnosticCode == "padstack_layer_update_unsupported") &&
    unsupported.Any(item => item.DiagnosticCode == "padstack_targeted_delete_unsupported"),
    "Vendor-unsupported operations must be disclosed with exact codes.");
Check(unsupported.All(item =>
    string.Equals(EnginePhysicalSymbolCapabilities.For(
        item.Operation == nameof(EnginePadstackWorkflowOperation.UpdateLayerGeometry)
            ? EnginePadstackWorkflowOperation.UpdateLayerGeometry
            : EnginePadstackWorkflowOperation.DeleteDefinition).DiagnosticCode,
        item.DiagnosticCode, StringComparison.Ordinal)),
    "Unsupported rows must match capability truth.");

Console.WriteLine($"PD padstack tool checks passed ({checks} checks).");

PadstackToolAvailability Find(ImmutableArray<PadstackToolAvailability> actions, string id) =>
    actions.Single(action => action.ActionId == id);

DesignScene PadstackScene()
{
    LayerId top = new("ETCH/TOP");
    LayerId bottom = new("ETCH/BOTTOM");
    var data = new SceneData
    {
        Components = [new(new("component:u1"), "U1", "PKG", "PART", null, 1, new(2, 2), new(0), "placed", false)],
        Nets = [new(new("net:gnd"), "GND", 2)],
        Pins = [new(new("pin:u1a1"), "U1", "A1", "GND", new(2, 2))],
        Layers = [new(top, 0, false, true, true), new(bottom, 1, false, true, false)],
        Padstacks =
        [
            new(new("padstack:a"), "VIA_A", new(10), true,
                [new(top, "regular", PolygonGeometry.Rectangle(new(new(-8, -8), new(8, 8)))),
                    new(bottom, "regular", PolygonGeometry.Rectangle(new(new(-8, -8), new(8, 8))))]),
            new(new("padstack:b"), "VIA_B", null, null,
                [new(top, "regular", PolygonGeometry.Rectangle(new(new(-5, -5), new(5, 5))))]),
        ],
        Copper =
        [
            new(new("via:a"), CopperKind.Via, "GND", null,
                new(new(0, 0), new(4, 4)), null, null,
                new("VIA_A", new(2, 2), [top, bottom], [top, bottom], "not_started", true,
                    new(true, null, null, null, null, null, []), new(0), false),
                [], null, []),
            new(new("copper:u1a1"), CopperKind.Pin, "GND", top,
                new(new(0, 0), new(4, 4)), null, null, null, [], null, [],
                new("U1", "A1", "VIA_A", new(2, 2), true, [top, bottom], []), null),
        ],
        Symbols = [new(new("symbol:pkg"), "PKG", [new("A1", "VIA_A", new(0, 0), new(0))], [])],
    };
    DataFamily[] unavailable = [DataFamily.BoardGeometry, DataFamily.Stackup, DataFamily.Routes, DataFamily.Contacts];
    var coverage = new CoverageReport(Enum.GetValues<DataFamily>().Select(family =>
        unavailable.Contains(family)
            ? new FamilyCoverage(family, DataAvailability.Unavailable, DataCompleteness.Partial,
                GeometryFidelity.Unknown, ["The lane F PD fixture does not model this canonical family."])
            : new FamilyCoverage(family, DataAvailability.Available,
                DataCompleteness.CompleteForRequestedScope, GeometryFidelity.AnalyticPrimitive, [])));
    return new(new(Guid.NewGuid(), DateTimeOffset.UtcNow,
            new("lane-f-pd-fixture", "1", true, "synthetic-data-only")),
        new(DocumentKind.PcbBoard, "lane-f-pd", "mils", 2, new(new(0, 0), new(100, 100))),
        SceneQuery.CompleteBoard() with { Families = Enum.GetValues<DataFamily>().Except(unavailable).ToImmutableArray() },
        coverage, data);
}
