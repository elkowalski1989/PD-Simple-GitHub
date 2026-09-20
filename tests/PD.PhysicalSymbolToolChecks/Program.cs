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

Check(PhysicalSymbolTool.Registration.ToolId == "tools.physical-symbols" &&
    PhysicalSymbolTool.Registration.Category == "Physical" &&
    PhysicalSymbolTool.Registration.OpensOffline &&
    !string.IsNullOrWhiteSpace(PhysicalSymbolTool.Registration.OfflineMode),
    "Tool registration data is wrong.");

EnginePhysicalSymbolOperation[] operations = Enum.GetValues<EnginePhysicalSymbolOperation>();
Check(operations.Length == 17, "The source-backed candidate inventory must hold exactly 17 operations.");

ImmutableArray<PhysicalSymbolToolAvailability> disconnected =
    PhysicalSymbolTool.DescribeActions(null, false, null);
Check(disconnected.Length == 3 + operations.Length + 1 &&
    disconnected.All(action => !action.Available) &&
    disconnected.All(action => !string.IsNullOrWhiteSpace(action.Title) &&
        !string.IsNullOrWhiteSpace(action.Reason) &&
        !string.IsNullOrWhiteSpace(action.NextStep)),
    "Disconnected actions must all be unavailable with title, reason, and next step.");
Check(disconnected.Select(action => action.ActionId).Distinct().Count() == disconnected.Length,
    "Action identifiers must be unique.");
Check(disconnected.Select(action => action.Title).Distinct().Count() == disconnected.Length,
    "Action titles must be unique.");

foreach (EnginePhysicalSymbolOperation operation in operations)
{
    string id = PhysicalSymbolToolActions.ForOperation(operation);
    PhysicalSymbolToolAvailability action = disconnected.Single(item => item.ActionId == id);
    EnginePhysicalSymbolCapabilityDiagnostic diagnostic = EnginePhysicalSymbolCapabilities.For(operation);
    Check(string.Equals(action.Reason, diagnostic.Limitation, StringComparison.Ordinal),
        $"Operation {operation} must report the exact Engine capability limitation.");
    Check(!action.Reason.Contains(".v1", StringComparison.Ordinal) &&
        !action.Reason.Contains("physical-symbol.", StringComparison.Ordinal),
        $"Operation {operation} must not leak native command identifiers.");
    Check(action.NextStep.Contains("T10-02", StringComparison.Ordinal),
        $"Operation {operation} must route to the open native acceptance gate.");
}
Check(EnginePhysicalSymbolCapabilities.ProductionSupportedOperations.Count == 0,
    "The Engine production-supported list must stay empty until native acceptance closes.");
Check(EnginePhysicalSymbolCapabilities.PackagedAcceptanceCandidates.Count == operations.Length,
    "Every candidate operation must remain a packaged acceptance candidate.");
Check(operations
        .Select(operation => EnginePhysicalSymbolCapabilities.For(operation).Group)
        .Distinct()
        .Count() == Enum.GetValues<EnginePhysicalSymbolCapabilityGroup>().Length,
    "The operation inventory must cover every capability group.");

DesignScene scene = SymbolScene();
ImmutableArray<PhysicalSymbolDefinitionSummary> summaries = PhysicalSymbolTool.SummarizeDefinitions(scene);
Check(summaries.Length == 2 && summaries[0].Name == "CASE_QFP" && summaries[1].Name == "PKG_EMPTY",
    "Definition summaries are missing or unsorted.");
PhysicalSymbolDefinitionSummary qfp = summaries[0];
Check(qfp.PinCount == 4 && qfp.HasPins &&
    qfp.Padstacks.SequenceEqual(["PAD_A"]),
    "The four-pad fixture summary is wrong.");
Check(!summaries[1].HasPins && summaries[1].Padstacks.IsEmpty,
    "The empty definition summary is wrong.");
Check(PhysicalSymbolTool.SummarizeDefinitions(null).IsEmpty, "Null scene must summarize to empty.");

ImmutableArray<PhysicalSymbolToolAvailability> offline =
    PhysicalSymbolTool.DescribeActions(scene, false, null);
Check(Find(offline, PhysicalSymbolToolActions.InspectDefinitions).Available,
    "Offline definition inspection must be available on a capture with definitions.");
Check(Find(offline, PhysicalSymbolToolActions.InspectDefinitions).NextStep.Contains("staging", StringComparison.OrdinalIgnoreCase),
    "Offline inspection must route toward staging.");
Check(!Find(offline, PhysicalSymbolToolActions.VerifySource).Available &&
    Find(offline, PhysicalSymbolToolActions.VerifySource).Reason.Contains("board instance", StringComparison.Ordinal),
    "Source verification without a staged document must name the board-instance boundary.");
Check(!Find(offline, PhysicalSymbolToolActions.StageWorkArea).Available &&
    Find(offline, PhysicalSymbolToolActions.StageWorkArea).Reason.Contains("not connected", StringComparison.Ordinal),
    "Staging while disconnected must name the connection gate.");

ImmutableArray<PhysicalSymbolToolAvailability> live =
    PhysicalSymbolTool.DescribeActions(scene, true, null);
Check(!Find(live, PhysicalSymbolToolActions.StageWorkArea).Available &&
    Find(live, PhysicalSymbolToolActions.StageWorkArea).Reason.Contains("T10-03", StringComparison.Ordinal),
    "Live staging must route to the open staged-document gate.");
Check(operations.All(operation =>
        !Find(live, PhysicalSymbolToolActions.ForOperation(operation)).Available),
    "No candidate operation may enable before native acceptance.");

ImmutableArray<PhysicalSymbolToolAvailability> staged =
    PhysicalSymbolTool.DescribeActions(scene, true, "CASE_QFP");
Check(!Find(staged, PhysicalSymbolToolActions.VerifySource).Available &&
    Find(staged, PhysicalSymbolToolActions.VerifySource).Reason.Contains("T10-03", StringComparison.Ordinal),
    "Staged source verification must route to the open native gate.");
PhysicalSymbolToolAvailability publish = Find(staged, PhysicalSymbolToolActions.PublishDra);
Check(!publish.Available &&
    publish.Reason.Contains("approval", StringComparison.Ordinal) &&
    publish.Reason.Contains("axlCompileSymbol", StringComparison.Ordinal),
    "Publication must require approval evidence and disclose the PSM vendor limit.");

ImmutableArray<PhysicalSymbolVendorLimit> limits = PhysicalSymbolTool.VendorLimits;
Check(limits.Length == 3 &&
    limits.Any(item => item.DiagnosticCode == "library_compile_contract_insufficient") &&
    limits.All(item => !string.IsNullOrWhiteSpace(item.Operation) &&
        !string.IsNullOrWhiteSpace(item.DiagnosticCode) &&
        !string.IsNullOrWhiteSpace(item.Limitation)),
    "Vendor limits must disclose the exact diagnostic codes.");

Console.WriteLine($"PD physical-symbol tool checks passed ({checks} checks).");

PhysicalSymbolToolAvailability Find(ImmutableArray<PhysicalSymbolToolAvailability> actions, string id) =>
    actions.Single(action => action.ActionId == id);

DesignScene SymbolScene()
{
    LayerId top = new("ETCH/TOP");
    LayerId bottom = new("ETCH/BOTTOM");
    var data = new SceneData
    {
        Components = [new(new("component:u1"), "U1", "CASE_QFP", "PART", null, 1, new(2, 2), new(0), "placed", false)],
        Nets = [new(new("net:gnd"), "GND", 2)],
        Pins = [new(new("pin:u1a1"), "U1", "1", "GND", new(2, 2))],
        Layers = [new(top, 0, false, true, true), new(bottom, 1, false, true, false)],
        Padstacks =
        [
            new(new("padstack:a"), "PAD_A", new(10), true,
                [new(top, "regular", PolygonGeometry.Rectangle(new(new(-8, -8), new(8, 8))))]),
        ],
        Copper =
        [
            new(new("copper:u1a1"), CopperKind.Pin, "GND", top,
                new(new(0, 0), new(4, 4)), null, null, null, [], null, [],
                new("U1", "1", "PAD_A", new(2, 2), true, [top, bottom], []), null),
        ],
        Symbols =
        [
            new(new("symbol:case"), "CASE_QFP",
                [new("1", "PAD_A", new(0, 0), new(0)),
                    new("2", "PAD_A", new(10, 0), new(0)),
                    new("3", "PAD_A", new(10, 10), new(0)),
                    new("4", "PAD_A", new(0, 10), new(0))],
                []),
            new(new("symbol:empty"), "PKG_EMPTY", [], []),
        ],
    };
    DataFamily[] unavailable = [DataFamily.BoardGeometry, DataFamily.Stackup, DataFamily.Routes, DataFamily.Contacts];
    var coverage = new CoverageReport(Enum.GetValues<DataFamily>().Select(family =>
        unavailable.Contains(family)
            ? new FamilyCoverage(family, DataAvailability.Unavailable, DataCompleteness.Partial,
                GeometryFidelity.Unknown, ["The lane E PD fixture does not model this canonical family."])
            : new FamilyCoverage(family, DataAvailability.Available,
                DataCompleteness.CompleteForRequestedScope, GeometryFidelity.AnalyticPrimitive, [])));
    return new(new(Guid.NewGuid(), DateTimeOffset.UtcNow,
            new("lane-e-pd-fixture", "1", true, "synthetic-data-only")),
        new(DocumentKind.PcbBoard, "lane-e-pd", "mils", 2, new(new(0, 0), new(100, 100))),
        SceneQuery.CompleteBoard() with { Families = Enum.GetValues<DataFamily>().Except(unavailable).ToImmutableArray() },
        coverage, data);
}
