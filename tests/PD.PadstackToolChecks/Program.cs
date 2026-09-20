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

void Expect<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        checks++;
        return;
    }
    catch (Exception error)
    {
        throw new InvalidOperationException($"{message} (wrong exception: {error.GetType().Name}: {error.Message})");
    }
    throw new InvalidOperationException(message);
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

// ---- Release rebind: EnginePadstackWorkflows dispatch + inspection ----
ImmutableArray<EnginePadstackCatalogRow> catalog = PadstackTool.CatalogStatus();
Check(catalog.Length == 8 && catalog.Select(row => row.Operation).Distinct().Count() == 8,
    "The Engine padstack catalog must hold eight distinct operations.");
Check(catalog.Single(row => row.Operation == EnginePadstackWorkflowOperation.UpdateLayerGeometry).Qualification ==
    EngineNativeQualification.Unsupported,
    "Layer-geometry editing must stay vendor-unsupported in the catalog.");
Check(catalog.Single(row => row.Operation == EnginePadstackWorkflowOperation.PurgeUnusedDefinitions).Note
    .Contains("never targeted delete", StringComparison.Ordinal),
    "The purge catalog row must disown targeted deletion.");

EnginePadstackDefinitionView inspected = PadstackTool.InspectDefinition(scene, "VIA_A");
Check(inspected.Found && inspected.Name == "VIA_A" && inspected.Layers.Length == 2,
    "Engine definition inspection must find the captured definition with its layers.");
EnginePadstackDefinitionView missing = PadstackTool.InspectDefinition(scene, "MISSING");
Check(!missing.Found && missing.Notes.Length != 0,
    "A missing definition must report not-found with notes, not throw.");
EnginePadstackUsage usage = PadstackTool.InspectInstances(scene, "VIA_A");
Check(usage.BoardViaCount == 1 && usage.BoardPinCount == 1 && usage.SymbolPinCount == 1 &&
    usage.SceneStamp.Length != 0,
    "Engine instance inspection must count board vias, board pins, and symbol pins with a stamp.");
EnginePadstackComparison same = PadstackTool.CompareDefinitions(scene, "VIA_A", "VIA_A");
Check(same.Equal && same.Deltas.IsEmpty,
    "A definition compared with itself must be equal with no deltas.");
EnginePadstackComparison diff = PadstackTool.CompareDefinitions(scene, "VIA_A", "VIA_B");
Check(!diff.Equal && !diff.Deltas.IsEmpty,
    "Distinct definitions must compare unequal with deltas.");
string diagnosis = PadstackTool.ExportDiagnosis(scene, "VIA_A", "VIA_B");
Check(diagnosis.Contains("Padstack diagnostic export", StringComparison.Ordinal) &&
    diagnosis.Contains("VIA_A", StringComparison.Ordinal) &&
    diagnosis.Contains("Where-used:", StringComparison.Ordinal),
    "The Engine diagnostic export must carry definition and where-used evidence.");

EnginePadstackGlobalEditPlan editPlan = PadstackTool.PlanGlobalEdit(
    "VIA_A", [new("drillDiameter", "12")], scene);
Check(editPlan.DefinitionVerifiedInCapture && editPlan.NativeCall.Contains("axlPadstackEdit", StringComparison.Ordinal) &&
    editPlan.Cautions.Any(caution => caution.Contains("every instance", StringComparison.Ordinal)),
    "The global-edit plan must verify the capture, name the pending native call, and warn about shared definitions.");
Check(PadstackTool.DescribePlan(editPlan).Contains("Planning only", StringComparison.Ordinal),
    "The global-edit summary must state that planning is not execution.");
EnginePadstackGlobalEditPlan unverifiedPlan = PadstackTool.PlanGlobalEdit(
    "VIA_A", [new("plating", "PLATED")], null);
Check(!unverifiedPlan.DefinitionVerifiedInCapture &&
    unverifiedPlan.Cautions.Any(caution => caution.Contains("not verified", StringComparison.Ordinal)),
    "A plan without a capture must stay unverified with a caution.");
Expect<ArgumentException>(() => PadstackTool.PlanGlobalEdit("VIA_A", [new("nope", "1")], null),
    "An undocumented attribute must be rejected.");
Expect<NotSupportedException>(() => PadstackTool.PlanGlobalEdit("VIA_A", [new("keepout", "1")], null),
    "The obsolete keepout attribute must be refused.");
Expect<ArgumentException>(() => PadstackTool.PlanGlobalEdit("VIA_A", [], null),
    "An empty change list must be rejected.");

string stamp = EnginePadstackInspection.Stamp(scene);
Check(stamp.Length != 0, "The Engine capture stamp must be non-empty.");
EnginePadstackPurgePlan purgePlan = PadstackTool.PlanPurge(EnginePadstackPurgeMode.AllUnused, stamp);
Check(purgePlan.NativeCall.Contains("axlPurgePadstacks", StringComparison.Ordinal) &&
    purgePlan.Cautions.Any(caution => caution.Contains("Global purge only", StringComparison.Ordinal)),
    "The purge plan must name the global native call with global-only cautions.");
Check(PadstackTool.DescribePlan(purgePlan).Contains("never targeted delete", StringComparison.Ordinal),
    "The purge summary must disown targeted deletion.");
Expect<ArgumentException>(() => PadstackTool.PlanPurge(EnginePadstackPurgeMode.DerivedOnly, "  "),
    "A blank usage stamp must be rejected.");

EnginePadstackDeletionAssessment inUse = PadstackTool.AssessTargetedDelete(true);
Check(!inUse.IsSupported && inUse.DiagnosticCode == "padstack_definition_in_use",
    "An in-use definition must be explicitly non-deletable.");
EnginePadstackDeletionAssessment unused = PadstackTool.AssessTargetedDelete(false);
Check(!unused.IsSupported && unused.DiagnosticCode == "padstack_targeted_delete_unsupported",
    "Even an unused definition must refuse targeted deletion; only global purge applies.");
Check(PadstackTool.ParsePurgeReceipt(3) == 3, "A purged count receipt must parse to its count.");
Expect<InvalidDataException>(() => PadstackTool.ParsePurgeReceipt(null),
    "A missing purge receipt must be rejected, never treated as success.");
Expect<InvalidDataException>(() => PadstackTool.ParsePurgeReceipt(-1),
    "A negative purge count must be rejected.");

EnginePadstackRedefinePlan redefine = PadstackTool.PlanRedefinition("VIA_A", "VIA_C", 2, true);
Check(redefine.Steps.Length == 3 &&
    redefine.Steps[2].Contains("only if truly unused", StringComparison.Ordinal) &&
    redefine.RollbackBoundaries.Length != 0,
    "Redefinition must plan three steps with a conditional purge tail and rollback boundaries.");
Check(PadstackTool.DescribePlan(redefine).Contains("by replacement", StringComparison.Ordinal),
    "The redefine summary must present replacement, not in-place editing.");
Expect<ArgumentException>(() => PadstackTool.PlanRedefinition("VIA_A", "VIA_A", 0, false),
    "Redefinition onto the same name must be rejected.");

EnginePadstackPendingNativePlan boardPin = PadstackTool.RequestBoardPinReplacement("U1", "A1", "VIA_B");
Check(boardPin.DiagnosticCode == "board_pin_replace_native_owner_missing" &&
    boardPin.ValidatedFacts.Any(fact => fact.Contains("U1:A1", StringComparison.Ordinal)),
    "A board-pin request must validate and stay explicitly unresolved.");
EnginePadstackPendingNativePlan symbolPin = PadstackTool.RequestSymbolPinReplacement("PKG", "A1", "VIA_A", "VIA_B");
Check(symbolPin.DiagnosticCode == "physical_symbol_native_acceptance_pending",
    "A symbol-pin request must route to the lane E acceptance path.");
Expect<ArgumentException>(() => PadstackTool.RequestSymbolPinReplacement("PKG", "A1", "VIA_A", "VIA_A"),
    "A no-change pin replacement must be rejected.");
EnginePadstackPendingNativePlan create = PadstackTool.RequestCreateDefinition("VIA_C", "10 mils", 2);
Check(create.Operation == EnginePadstackWorkflowOperation.CreateDefinition,
    "A create-definition request must validate against the lane E path.");
Expect<ArgumentException>(() => PadstackTool.RequestCreateDefinition("VIA_\u0001C", null, 0),
    "A control-character definition name must be rejected.");

string stagingRoot = Path.GetTempPath();
var libraryRequest = new EnginePadstackLibraryRequest(
    "VIA_A", "via_a", stagingRoot, stagingRoot, EnginePadstackOverwritePolicy.Refuse);
Check(PadstackTool.ValidateLibraryRequest(libraryRequest) == libraryRequest,
    "A valid library request must validate unchanged.");
Expect<ArgumentException>(() => PadstackTool.ValidateLibraryRequest(
    libraryRequest with { StagingRoot = "relative" }),
    "A relative staging root must be rejected.");
Check(PadstackTool.LibraryWriteCall("VIA_A", "via_a").Contains("axlPadstackToDisk", StringComparison.Ordinal),
    "The library write call must name the native function.");

Expect<ArgumentNullException>(() => PadstackTool.InspectDefinition(null!, "VIA_A"),
    "A null scene must be rejected by definition inspection.");
Expect<ArgumentNullException>(() => PadstackTool.ExportDiagnosis(null!, "VIA_A"),
    "A null scene must be rejected by diagnostic export.");

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
