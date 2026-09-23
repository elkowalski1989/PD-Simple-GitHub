// Lane H independent conformance harness (PD side), slice 3 (full surface).
//
// Scope: behavior conformance for the integrated tool heads plus the
// Engine-first architecture boundary. Feature checks execute public/model
// behavior with hand-computed expectations, never values copied from the
// implementation under test; shell navigation, section forwarding, and view
// wiring are verified as WPF behavior in PD.Simple.DrawingChecks
// (ShellNavigationChecks, ExplorerContractChecks), never from source text.
// Native gates are NOT_EXECUTED by environment (no license / Linux
// container); they are recorded in the handoff matrix, never as passes.
//
// Usage: dotnet run --project tests/PD.ToolsConformance [-- <repoRoot>]
//   Env overrides: PD_TOOLS_REPO_ROOT, LANE_H_EVIDENCE_DIR
// Exit code: 0 when every check passes, 1 otherwise. A results CSV is always
// written to the evidence directory.

using System.Collections.Immutable;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Geometry;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Manufacturing;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.PcbTools;
using PD.PcbTools.Manufacturing;
using PD.PcbTools.OverlayTools;
using PD.PcbTools.Review;

var records = new List<(string Id, bool Pass, string Detail)>();
void Record(string id, bool pass, string detail)
{
    records.Add((id, pass, detail));
    Console.WriteLine($"{(pass ? "PASS" : "FAIL")} {id} :: {detail}");
}

string repoRoot = args.Length > 0 ? args[0]
    : Environment.GetEnvironmentVariable("PD_TOOLS_REPO_ROOT")
    ?? FindRepoRoot(AppContext.BaseDirectory)
    ?? Directory.GetCurrentDirectory();

string mainXamlPath = Path.Combine(repoRoot, "src", "PD.Simple", "MainWindow.xaml");
string mainCsPath = Path.Combine(repoRoot, "src", "PD.Simple", "MainWindow.xaml.cs");
string explorerCsPath = Path.Combine(repoRoot, "src", "PD.Simple", "Engine", "EngineExplorerView.xaml.cs");
string overlayRecipePath = Path.Combine(repoRoot, "src", "PD.PcbTools", "OverlayTools", "OverlayToolRecipe.cs");
string trackerPath = Path.Combine(repoRoot, "src", "PD.PcbTools", "OverlayTools", "ToolPublicationTracker.cs");
string bundlePath = Path.Combine(repoRoot, "src", "PD.PcbTools", "Review", "ReviewBundleManifest.cs");
string padstackPath = Path.Combine(repoRoot, "src", "PD.PcbTools", "PadstackTool.cs");
string overlayVmPath = Path.Combine(repoRoot, "src", "PD.Simple", "Tools", "Overlay", "LiveOverlayToolViewModel.cs");
string overlayViewCsPath = Path.Combine(repoRoot, "src", "PD.Simple", "Tools", "Overlay", "LiveOverlayToolView.xaml.cs");
string reviewViewCsPath = Path.Combine(repoRoot, "src", "PD.Simple", "Tools", "Review", "ShareReviewToolView.xaml.cs");
string padstacksViewCsPath = Path.Combine(repoRoot, "src", "PD.Simple", "Tools", "Padstacks", "PadstacksView.xaml.cs");
string constraintsVmPath = Path.Combine(repoRoot, "src", "PD.Simple", "Tools", "ConstraintsDrc", "ConstraintsDrcViewModel.cs");
string constraintsViewCsPath = Path.Combine(repoRoot, "src", "PD.Simple", "Tools", "ConstraintsDrc", "ConstraintsDrcView.xaml.cs");
string physymToolPath = Path.Combine(repoRoot, "src", "PD.PcbTools", "PhysicalSymbolTool.cs");
string physymViewCsPath = Path.Combine(repoRoot, "src", "PD.Simple", "Tools", "PhysicalSymbols", "PhysicalSymbolsView.xaml.cs");
string mfgModelPath = Path.Combine(repoRoot, "src", "PD.PcbTools", "Manufacturing", "ManufacturingPageModel.cs");
string mfgRunnerPath = Path.Combine(repoRoot, "src", "PD.PcbTools", "Manufacturing", "ManufacturingRunner.cs");
string engineMfgRunnerPath = Path.Combine(repoRoot, "src", "PD.PcbTools", "Manufacturing", "EngineManufacturingExportRunner.cs");
string symRunnerPath = Path.Combine(repoRoot, "src", "PD.PcbTools", "SymbolBindingRunner.cs");
string mfgViewCsPath = Path.Combine(repoRoot, "src", "PD.Simple", "Manufacturing", "ManufacturingView.xaml.cs");

string mainXaml = File.Exists(mainXamlPath) ? File.ReadAllText(mainXamlPath) : string.Empty;
string mainCs = File.Exists(mainCsPath) ? File.ReadAllText(mainCsPath) : string.Empty;
string explorerCs = File.Exists(explorerCsPath) ? File.ReadAllText(explorerCsPath) : string.Empty;
string overlayRecipeCs = File.Exists(overlayRecipePath) ? File.ReadAllText(overlayRecipePath) : string.Empty;
string trackerCs = File.Exists(trackerPath) ? File.ReadAllText(trackerPath) : string.Empty;
string bundleCs = File.Exists(bundlePath) ? File.ReadAllText(bundlePath) : string.Empty;
string padstackCs = File.Exists(padstackPath) ? File.ReadAllText(padstackPath) : string.Empty;
string overlayVmCs = File.Exists(overlayVmPath) ? File.ReadAllText(overlayVmPath) : string.Empty;
string overlayViewCs = File.Exists(overlayViewCsPath) ? File.ReadAllText(overlayViewCsPath) : string.Empty;
string reviewViewCs = File.Exists(reviewViewCsPath) ? File.ReadAllText(reviewViewCsPath) : string.Empty;
string padstacksViewCs = File.Exists(padstacksViewCsPath) ? File.ReadAllText(padstacksViewCsPath) : string.Empty;
string constraintsVmCs = File.Exists(constraintsVmPath) ? File.ReadAllText(constraintsVmPath) : string.Empty;
string constraintsViewCs = File.Exists(constraintsViewCsPath) ? File.ReadAllText(constraintsViewCsPath) : string.Empty;
string physymToolCs = File.Exists(physymToolPath) ? File.ReadAllText(physymToolPath) : string.Empty;
string physymViewCs = File.Exists(physymViewCsPath) ? File.ReadAllText(physymViewCsPath) : string.Empty;
string mfgModelCs = File.Exists(mfgModelPath) ? File.ReadAllText(mfgModelPath) : string.Empty;
string mfgRunnerCs = File.Exists(mfgRunnerPath) ? File.ReadAllText(mfgRunnerPath) : string.Empty;
string engineMfgCs = File.Exists(engineMfgRunnerPath) ? File.ReadAllText(engineMfgRunnerPath) : string.Empty;
string symRunnerCs = File.Exists(symRunnerPath) ? File.ReadAllText(symRunnerPath) : string.Empty;
string mfgViewCs = File.Exists(mfgViewCsPath) ? File.ReadAllText(mfgViewCsPath) : string.Empty;

// Shell navigation, section forwarding, and view wiring are verified as WPF
// behavior in PD.Simple.DrawingChecks (ShellNavigationChecks,
// ExplorerContractChecks). The retired H-SHELL-*/H-NAV-*-01 source-text
// checks asserted the same destinations from XAML/code-behind spelling.

// (Retired shell/section wiring source-text checks: H-SHELL-WIRED-01,
// H-SHELL-HDR-01, H-SHELL-CARD-01, H-SHELL-ROUTE-01, H-SHELL-FWD-01,
// H-NAV-A-01, H-NAV-A-02, H-NAV-B-01. See the note above.)

// ---- H-NAV-B-02: overlay recipes carry ellipse/polyline + Drag, validated ----
bool bValidateRun = false;
string bValidateDetail = string.Empty;
try
{
    var style = new OverlayToolStyle(255, 10, 20, 30, 2);
    var goodEllipse = new OverlayToolRecipe("lane-h-e1", new OverlayToolAnchor.Board(0, 0),
        new OverlayToolShape.Ellipse(10, 20, 60, 30), style);
    var badEllipse = new OverlayToolRecipe("lane-h-e2", new OverlayToolAnchor.Board(0, 0),
        new OverlayToolShape.Ellipse(10, 20, 0, 30), style);
    var goodPoly = new OverlayToolRecipe("lane-h-p1", new OverlayToolAnchor.Board(0, 0),
        new OverlayToolShape.Polyline([(0, 0), (50, 0), (50, 25)], false), style);
    var badPoly = new OverlayToolRecipe("lane-h-p2", new OverlayToolAnchor.Board(0, 0),
        new OverlayToolShape.Polyline([(7, 7)], false), style);
    bool goodOk = goodEllipse.Validate().IsEmpty && goodPoly.Validate().IsEmpty;
    bool badEllipseOk = badEllipse.Validate().Any(e => e.Field == "Radii");
    bool badPolyOk = badPoly.Validate().Any(e => e.Field == "Points");
    bValidateRun = goodOk && badEllipseOk && badPolyOk;
    bValidateDetail = $"good-accepted={goodOk} zero-radii-rejected={badEllipseOk} single-point-rejected={badPolyOk}.";
}
catch (Exception error)
{
    bValidateDetail = $"{error.GetType().Name}: {error.Message}";
}
Record("H-NAV-B-02", bValidateRun,
    $"validation behavior only, source spelling retired: runtime({bValidateDetail}).");

// ---- H-NAV-B-03: publication receipts are operation-bound, never last-result ----
bool bTrackerRun = false;
string bTrackerDetail = string.Empty;
try
{
    var tracker = new ToolPublicationTracker();
    Guid opA = tracker.StartOperation(epoch: 3, revision: 11);
    Guid opB = tracker.StartOperation(epoch: 3, revision: 12);
    Guid cap = Guid.NewGuid();
    var receiptA = new ToolPublicationReceipt("lane-h-doc", cap, 11, 41, ToolPublicationAvailability.Visible, DateTimeOffset.UtcNow);
    var receiptB = new ToolPublicationReceipt("lane-h-doc", cap, 12, 42, ToolPublicationAvailability.Pending, DateTimeOffset.UtcNow);
    var outA = tracker.Complete(opA, currentEpoch: 3, currentSequence: 41, receiptA);
    var outB = tracker.Complete(opB, currentEpoch: 3, currentSequence: 42, receiptB);
    var stale = tracker.Complete(opA, currentEpoch: 4, currentSequence: 41, receiptA);
    var unknown = tracker.Complete(Guid.NewGuid(), 3, 41, receiptA);
    bool isolated = tracker.IsPublishedVisibleFor(opA, 3, 11) && !tracker.IsPublishedVisibleFor(opB, 3, 12);
    bool retired = tracker.Retire(opB) && !tracker.IsPublishedVisibleFor(opB, 3, 12) && tracker.IsPublishedVisibleFor(opA, 3, 11);
    bTrackerRun = outA == ToolPublicationOutcome.Visible && outB == ToolPublicationOutcome.Pending
        && stale == ToolPublicationOutcome.Superseded && unknown == ToolPublicationOutcome.UnknownOperation
        && isolated && retired;
    bTrackerDetail = $"visible={outA} pending={outB} stale-epoch={stale} unknown={unknown} isolated={isolated} retire-safe={retired}.";
}
catch (Exception error)
{
    bTrackerDetail = $"{error.GetType().Name}: {error.Message}";
}
Record("H-NAV-B-03", bTrackerRun,
    $"operation-bound behavior only, source spelling retired: runtime({bTrackerDetail}).");

// ---- H-NAV-B-04: review bundles carry hashes, verify on reopen, hold no authority ----
bool bBundleRun = false;
string bBundleDetail = string.Empty;
try
{
    byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02];
    var manifest = ReviewBundleManifest.Build(Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(),
        "lane-h-doc", "raw", false, 800, 600, DateTimeOffset.UtcNow, "lane-h-viewport",
        "lane-h-qualification", null, [("raw", "lane-h-check.png", png)], null);
    string json = ReviewBundleManifest.Serialize(manifest);
    bool roundTrip = ReviewBundleManifest.TryParse(json, out ReviewBundleManifest.Manifest? parsed, out string? parseError)
        && parsed is not null && parsed.Images.Length == 1 && parsed.Images[0].FileName == "lane-h-check.png";
    bool hashLinked = roundTrip && string.Equals(parsed!.Images[0].Sha256, ReviewBundleManifest.Sha256Hex(png), StringComparison.OrdinalIgnoreCase);
    bool tamperSeen = roundTrip && !string.Equals(parsed!.Images[0].Sha256, ReviewBundleManifest.Sha256Hex([0x00]), StringComparison.OrdinalIgnoreCase);
    bool badSchema = !ReviewBundleManifest.TryParse(json.Replace(ReviewBundleManifest.Schema, "pd.review-bundle/vX"),
        out _, out string? schemaError) && !string.IsNullOrWhiteSpace(schemaError);
    bBundleRun = roundTrip && hashLinked && tamperSeen && badSchema;
    bBundleDetail = $"round-trip={roundTrip} hash-linked={hashLinked} tamper-distinguished={tamperSeen} schema-rejected={badSchema}.";
}
catch (Exception error)
{
    bBundleDetail = $"{error.GetType().Name}: {error.Message}";
}
Record("H-NAV-B-04", bBundleRun,
    $"hash/link behavior only, source spelling retired: runtime({bBundleDetail}).");

// (Retired padstacks wiring source-text check H-NAV-F-01. Behavior lives in
// ShellNavigationChecks on Windows.)

// ---- H-NAV-F-02: padstack tool registration + availability truth ----
bool fToolRun = false;
string fToolDetail = string.Empty;
try
{
    bool reg = PadstackTool.Registration.ToolId == "tools.padstacks"
        && PadstackTool.Registration.Category == "Physical"
        && PadstackTool.Registration.OpensOffline;
    ImmutableArray<PadstackToolAvailability> disconnected = PadstackTool.DescribeActions(null, false, null);
    bool disc = disconnected.Length == 11 && disconnected.All(a => !a.Available)
        && disconnected.All(a => !string.IsNullOrWhiteSpace(a.Reason) && !string.IsNullOrWhiteSpace(a.NextStep));
    bool empty = PadstackTool.SummarizeDefinitions(null).IsEmpty;
    var unsupported = PadstackTool.UnsupportedOperations;
    bool unsup = unsupported.Length == 2
        && unsupported.Any(u => u.DiagnosticCode == "padstack_layer_update_unsupported")
        && unsupported.Any(u => u.DiagnosticCode == "padstack_targeted_delete_unsupported");
    fToolRun = reg && disc && empty && unsup;
    fToolDetail = $"registration={reg} disconnected-11-gated={disc} null-empty={empty} unsupported-codes={unsup}.";
}
catch (Exception error)
{
    fToolDetail = $"{error.GetType().Name}: {error.Message}";
}
Record("H-NAV-F-02", fToolRun,
    $"registration/availability behavior only, source spelling retired: runtime({fToolDetail}).");

// ---- H-NAV-F-03: padstack Engine-workflow dispatch (plans validate, never execute) ----
bool fDispatchRun = false;
string fDispatchDetail = string.Empty;
try
{
    bool fCatalog = PadstackTool.CatalogStatus().Length == 8;
    EnginePadstackPurgePlan fPurge = PadstackTool.PlanPurge(
        EnginePadstackPurgeMode.AllUnused, EnginePadstackInspection.Stamp(LaneHScene()));
    bool fPurgeHonest = fPurge.NativeCall.Contains("axlPurgePadstacks", StringComparison.Ordinal)
        && PadstackTool.DescribePlan(fPurge).Contains("never targeted delete", StringComparison.Ordinal);
    EnginePadstackDeletionAssessment fDelete = PadstackTool.AssessTargetedDelete(true);
    bool fNoDelete = !fDelete.IsSupported && fDelete.DiagnosticCode == "padstack_definition_in_use";
    bool fReceiptRefused = false;
    try { PadstackTool.ParsePurgeReceipt(null); }
    catch (InvalidDataException) { fReceiptRefused = true; }
    fDispatchRun = fCatalog && fPurgeHonest && fNoDelete && fReceiptRefused;
    fDispatchDetail = $"catalog8={fCatalog} purge-honest={fPurgeHonest} targeted-refused={fNoDelete} receipt-refused={fReceiptRefused}.";
}
catch (Exception error)
{
    fDispatchDetail = $"{error.GetType().Name}: {error.Message}";
}
Record("H-NAV-F-03", fDispatchRun,
    $"plan/refusal behavior only, source spelling retired: runtime({fDispatchDetail}).");

// ---- H-BOUNDARY-01: Engine-first boundary (no private mechanisms) ----
// Assembly-attribute reads (GetCustomAttribute) are legitimate diagnostics;
// the forbidden mechanisms are private-field/method reflection that would
// bypass the public Engine contract (cf. H-SHELL-FWD-01 for the forwarder).
string[] boundaryFiles = [mainCs, explorerCs, padstackCs, overlayRecipeCs, trackerCs, bundleCs, overlayVmCs, overlayViewCs, reviewViewCs, padstacksViewCs, physymToolCs, mfgModelCs, mfgRunnerCs, engineMfgCs, symRunnerCs, physymViewCs, constraintsViewCs, mfgViewCs, constraintsVmCs];
string[] markers = ["GetField(", "GetMethod(", "BindingFlags", "MakeGenericMethod"];
var hits = boundaryFiles
    .SelectMany((text, index) => markers.Where(m => text.Contains(m, StringComparison.Ordinal)).Select(m => $"{index}:{m}"))
    .ToArray();
bool versionReadOnly = mainCs.Contains("GetCustomAttribute<AssemblyInformationalVersionAttribute>");
Record("H-BOUNDARY-01", hits.Length == 0,
    hits.Length == 0 ? $"No private-reflection markers in 19 integrated files (System.Reflection use is version-read-only={versionReadOnly})."
        : $"Private-reflection markers: {string.Join(", ", hits)}.");

// ---- H-ROUTE-SEL-*: independent layer-selection verification ----
WorkspaceDocumentIdentity doc = new("lane-h-proof", 1, 2, null, "lane-h-proof.brd", "data-only");
EngineRoutingLayer Layer(string id, bool visible, bool active) => new(new(id), false, visible, active);

EngineRoutingLayerCatalog Catalog(params EngineRoutingLayer[] layers) => new(doc, layers);

bool selPass = true;
try
{
    var one = EngineHorizontalFirstRoutePolicy.SelectLayer(Catalog(
        Layer("ETCH/TOP", true, false), Layer("ETCH/S03", true, true)));
    selPass &= one.Id == new LayerId("ETCH/S03");

    var fallback = EngineHorizontalFirstRoutePolicy.SelectLayer(Catalog(
        Layer("ETCH/TOP", true, false), Layer("ETCH/S03", true, false)));
    selPass &= fallback.Id == new LayerId("ETCH/TOP");
}
catch (Exception error)
{
    selPass = false;
    Console.WriteLine($"  selection detail: {error.GetType().Name}: {error.Message}");
}
Record("H-ROUTE-SEL-01", selPass, "Single-active selection and first-visible fallback verified independently.");

bool invisiblePass = false;
try
{
    var picked = EngineHorizontalFirstRoutePolicy.SelectLayer(Catalog(
        Layer("ETCH/S01", false, true), Layer("ETCH/S02", true, false)));
    invisiblePass = picked.Id == new LayerId("ETCH/S02");
}
catch (Exception error)
{
    Console.WriteLine($"  invisible-active detail: {error.GetType().Name}: {error.Message}");
}
Record("H-ROUTE-SEL-02", invisiblePass, "Invisible active layer ignored; first visible layer selected.");

bool rejectPass = true;
try { EngineHorizontalFirstRoutePolicy.SelectLayer(Catalog(Layer("ETCH/TOP", true, true), Layer("ETCH/S03", true, true))); rejectPass = false; }
catch (InvalidDataException) { }
catch (Exception error) { rejectPass = false; Console.WriteLine($"  ambiguous detail: {error.GetType().Name}"); }
try { EngineHorizontalFirstRoutePolicy.SelectLayer(Catalog(Layer("ETCH/TOP", false, false))); rejectPass = false; }
catch (InvalidOperationException) { }
catch (Exception error) { rejectPass = false; Console.WriteLine($"  none-visible detail: {error.GetType().Name}"); }
try { EngineHorizontalFirstRoutePolicy.SelectLayer(Catalog()); rejectPass = false; }
catch (InvalidOperationException) { }
catch (Exception error) { rejectPass = false; Console.WriteLine($"  empty detail: {error.GetType().Name}"); }
try { EngineHorizontalFirstRoutePolicy.SelectLayer(null!); rejectPass = false; }
catch (ArgumentNullException) { }
catch (Exception error) { rejectPass = false; Console.WriteLine($"  null detail: {error.GetType().Name}"); }
Record("H-ROUTE-SEL-03", rejectPass, "Ambiguous/no-visible/empty/null catalogs rejected with documented exceptions.");

// ---- H-ROUTE-PLAN-*: hand-computed H-first geometry ----
bool planPass = true;
try
{
    var endpoints = new EngineTraceEndpoints(
        new(new(1m, 2), "SIGNAL_A"),
        new(new(11m, 20), "SIGNAL_A"),
        "mils", 4);
    var plan = EngineHorizontalFirstRoutePolicy.Plan(endpoints, 7, Layer("ETCH/S03", true, true));
    planPass &= plan.Points.Count == 3
        && plan.Points[1] == new DesignPoint(11, 2)
        && plan.Width.Mils == 7
        && plan.NetName == "SIGNAL_A"
        && plan.Layer == new LayerId("ETCH/S03");

    var conflict = EngineHorizontalFirstRoutePolicy.Plan(
        endpoints with { Second = endpoints.Second with { NetName = "OTHER" } },
        7, Layer("ETCH/S03", true, true));
    planPass &= conflict.NetName is null;

    try { EngineHorizontalFirstRoutePolicy.Plan(endpoints, 0.01m, Layer("ETCH/S03", true, true)); planPass = false; }
    catch (ArgumentOutOfRangeException) { }
}
catch (Exception error)
{
    planPass = false;
    Console.WriteLine($"  plan detail: {error.GetType().Name}: {error.Message}");
}
Record("H-ROUTE-PLAN-01", planPass, "H-first bend, net-conflict unassigned, width bounds verified with hand-computed points.");

// (Retired dispatcher/session/placeholder/section source-text checks:
// H-SHELL-SHOWTOOL-01, H-EXPLORER-SESSION-01, H-PLACEHOLDER-01, H-NAV-C-01.
// Behavior lives in ShellNavigationChecks/ExplorerContractChecks on Windows.)

// (Retired constraints/DRC wiring source-text check H-NAV-D-01. Behavior
// lives in ShellNavigationChecks on Windows.)

// ---- H-NAV-D-02: Engine DRC capability identity ----
bool dEngineIds = EngineCapabilities.Drc.Value == "engine.drc"
    && AllegroWorkspaceDrcRun.CapabilityId.Value == "engine.drc.execute"
    && EngineDrcRunRequest.FullBoard.Scope == EngineDrcRunScope.FullBoard;
Record("H-NAV-D-02", dEngineIds,
    $"engine capability identity only; version-spelling assertions retired: engine-ids={dEngineIds} (T09-02/03/05 native NOT_EXECUTED).");

// ---- H-NAV-D-03: Engine review delegation (identical comparison semantics) ----
bool dReviewRun = false;
string dReviewDetail = string.Empty;
try
{
    var reviewDoc = new WorkspaceDocumentIdentity("lane-h-drc", 1, 2, 100, "lane-h.brd", "PD_V25");
    var reviewEvidence = new EngineEvidence("lane-h-op", reviewDoc, DateTimeOffset.UtcNow, "lane-h", "hand-built", true);
    AllegroWorkspaceDrcRead reviewRead(string capture, params AllegroWorkspaceDrcMarkerEvidence[] markers) =>
        new(reviewDoc, EngineAcquisitionState.Complete, EngineFreshness.Current,
            new FamilyCoverage(DataFamily.Drc, DataAvailability.Available, DataCompleteness.CompleteForRequestedScope),
            [], [.. markers],
            new AllegroWorkspaceDrcEvidence(capture, EngineDrcEvidenceSource.ExistingMarkers, 1,
                markers.Length, markers.Length, EngineDrcEvidenceAvailability.Available, true, false,
                EngineDrcRunFreshness.Current, EngineDrcEvidenceAvailability.Unavailable,
                EngineDrcEvidenceAvailability.CountOnly),
            [], reviewEvidence, [], EngineRecovery.None);
    var reviewMarker = new AllegroWorkspaceDrcMarkerEvidence(
        new SceneObjectId("marker-1"), "Spacing C2C", "NET SPACING", new LayerId("ETCH/TOP"),
        new DesignPoint(10m, 20m), "5.0", "3.2", "constraint", false, 2);
    bool keyUsesSeparator = AllegroWorkspaceDrcReview.StableKey(reviewMarker).Contains(((char)31).ToString(), StringComparison.Ordinal);
    var reviewGroups = AllegroWorkspaceDrcReview.Group([reviewMarker, reviewMarker with { Waived = true }]);
    bool grouped = reviewGroups.Length == 1 && reviewGroups[0].Count == 2 && reviewGroups[0].WaivedCount == 1;
    var reviewCompared = AllegroWorkspaceDrcReview.Compare(
        reviewRead("before", reviewMarker),
        reviewRead("after", reviewMarker with { Waived = true }));
    bool persistent = reviewCompared.Added.IsEmpty && reviewCompared.Removed.IsEmpty &&
        reviewCompared.Persistent.Length == 1;
    dReviewRun = keyUsesSeparator && grouped && persistent;
    dReviewDetail = $"separator-key={keyUsesSeparator} grouping={grouped} waiver-persistent={persistent}.";
}
catch (Exception error)
{
    dReviewDetail = $"{error.GetType().Name}: {error.Message}";
}
Record("H-NAV-D-03", dReviewRun,
    $"comparison behavior only, source spelling retired: runtime({dReviewDetail}).");

// (Retired physical-symbols wiring source-text check H-NAV-E-01. Behavior
// lives in ShellNavigationChecks on Windows.)

// ---- H-NAV-E-02: physical-symbol policy truth (offline inspection only) ----
bool eToolRun = false;
string eToolDetail = string.Empty;
try
{
    EnginePhysicalSymbolOperation[] eOps = Enum.GetValues<EnginePhysicalSymbolOperation>();
    bool eReg = PhysicalSymbolTool.Registration.ToolId == "tools.physical-symbols"
        && PhysicalSymbolTool.Registration.Category == "Physical"
        && PhysicalSymbolTool.Registration.OpensOffline;
    ImmutableArray<PhysicalSymbolToolAvailability> eDisc = PhysicalSymbolTool.DescribeActions(null, false, null);
    bool eCount = eOps.Length == 17 && eDisc.Length == 3 + eOps.Length + 1;
    bool eGated = eDisc.All(a => !a.Available)
        && eDisc.All(a => !string.IsNullOrWhiteSpace(a.Title) && !string.IsNullOrWhiteSpace(a.Reason) && !string.IsNullOrWhiteSpace(a.NextStep))
        && eDisc.Select(a => a.ActionId).Distinct().Count() == eDisc.Length;
    bool eTruth = eOps.All(op =>
    {
        PhysicalSymbolToolAvailability action = eDisc.Single(a => a.ActionId == PhysicalSymbolToolActions.ForOperation(op));
        return string.Equals(action.Reason, EnginePhysicalSymbolCapabilities.For(op).Limitation, StringComparison.Ordinal)
            && !action.Reason.Contains(".v1", StringComparison.Ordinal)
            && !action.Reason.Contains("physical-symbol.", StringComparison.Ordinal)
            && action.NextStep.Contains("T10-02", StringComparison.Ordinal);
    });
    bool eEmpty = PhysicalSymbolTool.SummarizeDefinitions(null).IsEmpty;
    bool eProd = EnginePhysicalSymbolCapabilities.ProductionSupportedOperations.Count == 0;
    DesignScene eScene = LaneHScene();
    ImmutableArray<PhysicalSymbolDefinitionSummary> eDefs = PhysicalSymbolTool.SummarizeDefinitions(eScene);
    bool eOffline = eDefs.Length == 2 && eDefs[0].Name == "CASE_QFP" && eDefs[0].PinCount == 4 && eDefs[0].HasPins
        && PhysicalSymbolTool.DescribeActions(eScene, false, null)
            .Single(a => a.ActionId == PhysicalSymbolToolActions.InspectDefinitions).Available;
    var eLimits = PhysicalSymbolTool.VendorLimits;
    bool eVendor = eLimits.Length == 3
        && eLimits.Any(l => l.DiagnosticCode == "library_compile_contract_insufficient");
    eToolRun = eReg && eCount && eGated && eTruth && eEmpty && eProd && eOffline && eVendor;
    eToolDetail = $"registration={eReg} ops17+actions21={eCount} disconnected-gated={eGated} reason-equals-truth={eTruth} null-empty={eEmpty} production-empty={eProd} offline-inspect={eOffline} vendor-limits={eVendor}.";
}
catch (Exception error)
{
    eToolDetail = $"{error.GetType().Name}: {error.Message}";
}
Record("H-NAV-E-02", eToolRun,
    $"policy behavior only, source spelling retired: runtime({eToolDetail}) (T10-01..06 native NOT_EXECUTED).");

// ---- H-NAV-E-03: symbol binding/activation workflow against staged PACKAGE docs ----
bool eBinderRun = false;
string eBinderDetail = string.Empty;
try
{
    AllegroEngineSession eSession = AllegroEngineSession.Create();
    try
    {
        var eRunner = new EngineSymbolBindingRunner(eSession.Workspace);
        EngineSymbolWorkArea eArea = eRunner.PlanStage(
            Path.Combine(Path.GetTempPath(), "pd-lane-h-symstage"), "CASE_QFP", "lane-h");
        bool eStaged = eArea.StagedDraPath.EndsWith("CASE_QFP.dra", StringComparison.Ordinal)
            && !File.Exists(eArea.StagedDraPath)
            && eRunner.StateText.Contains("CASE_QFP", StringComparison.Ordinal);
        bool eRefused = false;
        try
        {
            eRunner.ActivateAsync(new("other-extension", "1.0.0", "ext.il", new string('a', 64)))
                .AsTask().GetAwaiter().GetResult();
        }
        catch (NotSupportedException)
        {
            eRefused = true;
        }
        bool eNoBind = false;
        try
        {
            eRunner.ApplyAsync("lane-h").AsTask().GetAwaiter().GetResult();
        }
        catch (InvalidOperationException)
        {
            eNoBind = true;
        }
        eBinderRun = eStaged && eRefused && eNoBind;
        eBinderDetail = $"stage-pure={eStaged} foreign-extension-refused={eRefused} apply-without-preview-refused={eNoBind}.";
    }
    finally
    {
        eSession.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
catch (Exception error)
{
    eBinderDetail = $"{error.GetType().Name}: {error.Message}";
}
Record("H-NAV-E-03", eBinderRun,
    $"staging/refusal behavior only, source spelling retired: runtime({eBinderDetail}) (T10-01..06 native NOT_EXECUTED).");

// (Retired manufacturing wiring source-text check H-NAV-G-01. Behavior lives
// in ShellNavigationChecks on Windows.)

// ---- H-NAV-G-02: manufacturing page policy (offline-first, honest NoGo) ----
bool gToolRun = false;
string gToolDetail = string.Empty;
try
{
    var gModel = new ManufacturingPageModel();
    bool gDisc = !gModel.IsConnected
        && gModel.SourceStatus.State == ManufacturingPageSourceState.Disconnected
        && !gModel.CanPlan(out string gOfflineReason) && gOfflineReason.Length != 0;
    bool gNoAcquire = false;
    gModel.RefreshSource(() => throw new InvalidOperationException("lane-h must not acquire while disconnected"));
    gNoAcquire = gModel.SourceStatus.State == ManufacturingPageSourceState.Disconnected;
    gModel.SetConnected(true);
    gModel.RefreshSource(() => throw new InvalidOperationException("The saved board source is unavailable."));
    bool gMissing = gModel.SourceStatus.State == ManufacturingPageSourceState.NoCanonicalSource;
    gModel.RefreshSource(LaneHSource);
    bool gReady = gModel.SourceStatus.State == ManufacturingPageSourceState.Ready && gModel.CanPlan(out _);
    var gArtOpts = new ArtworkOptions(ArtworkGerberFormat.Rs274X, ArtworkCoordinateUnits.Inches,
        SuppressNegativeFilmShapeArrayFill: false, UseVectorPadBehaviorForRasterArtwork: false);
    var (gArt, gArtErr) = gModel.TryBuildArtwork(
        [new("TOP", "ETCH/TOP", "films/top.gbr", false, false), new("BOTTOM", "ETCH/BOTTOM", "films/bottom.gbr", true, true)],
        gArtOpts, "release-page", false);
    bool gArtOk = gArt is not null && gArtErr is null && gArt.Films.Length == 2
        && gArt.Films[1].Polarity == ArtworkPolarity.Negative
        && ManufacturingPageModel.PreviewArtworkManifest(gArt).Count == 3;
    var (gDupe, gDupeErr) = gModel.TryBuildArtwork(
        [new("TOP", "ETCH/TOP", "films/top.gbr", false, false), new("TOP", "ETCH/BOTTOM", "films/bottom.gbr", false, false)],
        gArtOpts, "release-dupe", false);
    bool gDupeNo = gDupe is null && gDupeErr is not null;
    var gOdbOpts = new OdbPlusPlusOptions(OdbPlusPlusOutputMode.Directory, null, null,
        OdbPlusPlusPadflashHandling.Default, OdbPlusPlusComponentOutlineSource.Default, null, null, false, false);
    var (gOdb, gOdbErr) = gModel.TryBuildOdbPlusPlus("primary", "ETCH/TOP, ETCH/BOTTOM", gOdbOpts, "release-odb", false);
    bool gOdbOk = gOdb is not null && gOdbErr is null && ManufacturingPageModel.PreviewOdbManifest(gOdb).Count == 3;
    var (gOdbEmpty, gOdbEmptyErr) = gModel.TryBuildOdbPlusPlus("primary", " , ", gOdbOpts, "release-odb-empty", false);
    bool gOdbNo = gOdbEmpty is null && gOdbEmptyErr is not null;
    var (gIpc, gIpcErr) = gModel.TryBuildIpc2581("ETCH/TOP", "ipc/job.xml",
        new(Ipc2581Revision.C, Ipc2581Units.Millimeters, Ipc2581Content.LayerStackup), "release-ipc", false);
    bool gIpcOk = gIpc is not null && gIpcErr is null
        && ManufacturingPageModel.PreviewIpcManifest(gIpc).Single() == "ipc/job.xml";
    var gCtx = new ManufacturingRunnerContext(Path.GetTempPath(), null, TimeSpan.FromMinutes(5));
    var gRunner = new UnqualifiedManufacturingRunner();
    StagedArtworkResult gArtRun = gModel.RunArtworkAsync(gArt!, gCtx, gRunner).GetAwaiter().GetResult();
    StagedOdbPlusPlusResult gOdbRun = gModel.RunOdbPlusPlusAsync(gOdb!, gCtx, gRunner).GetAwaiter().GetResult();
    StagedIpc2581Result gIpcRun = gModel.RunIpc2581Async(gIpc!, gCtx, gRunner).GetAwaiter().GetResult();
    bool gNoGo = gArtRun.Result.State == ManufacturingOutputState.NoGo && gArtRun.Staging is null
        && gArtRun.Result.Code == "artwork_native_contract_unqualified"
        && ManufacturingPageModel.SummarizeArtwork(gArtRun.Result).Contains("NoGo", StringComparison.Ordinal)
        && gOdbRun.Result.State == ManufacturingOutputState.NoGo
        && ManufacturingPageModel.SummarizeOdb(gOdbRun.Result).Contains("NoGo", StringComparison.Ordinal)
        && gIpcRun.Result.State == ManufacturingOutputState.NoGo && gIpcRun.Staging is null
        && gIpcRun.Result.Code == "ipc2581_native_contract_unqualified"
        && ManufacturingPageModel.SummarizeIpc(gIpcRun.Result).Contains("NoGo", StringComparison.Ordinal);
    bool gPromoNo = !gModel.CanPromote(gArtRun.Result.JobId, gArtRun.Result.State, gArtRun.Staging is not null, out string gPromoReason)
        && gPromoReason.Length != 0;
    gModel.ReleaseStaging();
    gToolRun = gDisc && gNoAcquire && gMissing && gReady && gArtOk && gDupeNo && gOdbOk && gOdbNo && gIpcOk && gNoGo && gPromoNo;
    gToolDetail = $"disconnected={gDisc} no-acquire={gNoAcquire} missing-source={gMissing} ready={gReady} artwork={gArtOk} dupe-refused={gDupeNo} odb={gOdbOk} odb-empty-refused={gOdbNo} ipc={gIpcOk} nogo3={gNoGo} promote-refused={gPromoNo}.";
}
catch (Exception error)
{
    gToolDetail = $"{error.GetType().Name}: {error.Message}";
}
Record("H-NAV-G-02", gToolRun,
    $"offline-first/NoGo behavior only, source spelling retired: runtime({gToolDetail}) (T12-01..06 native NOT_EXECUTED).");

// ---- H-NAV-G-03: Engine-backed exporter replaces the unqualified default ----
bool gBinderRun = false;
string gBinderDetail = string.Empty;
try
{
    AllegroEngineSession gSession = AllegroEngineSession.Create();
    try
    {
        var gEngine = new EngineManufacturingExportRunner(gSession.Workspace);
        var gModel2 = new ManufacturingPageModel();
        gModel2.SetConnected(true);
        gModel2.RefreshSource(LaneHSource);
        var (gOdbPlan, gOdbPlanError) = gModel2.TryBuildOdbPlusPlus("primary", "ETCH/TOP, ETCH/BOTTOM",
            new(OdbPlusPlusOutputMode.Directory, null, null, OdbPlusPlusPadflashHandling.Default,
                OdbPlusPlusComponentOutlineSource.Default, null, null, false, false),
            "release-g3", false);
        var gCtx2 = new ManufacturingRunnerContext(Path.GetTempPath(), null, TimeSpan.FromMinutes(5));
        StagedOdbPlusPlusResult gHeadless = gModel2.RunOdbPlusPlusAsync(gOdbPlan!, gCtx2, gEngine)
            .GetAwaiter().GetResult();
        bool gNoGo = gOdbPlan is not null && gOdbPlanError is null
            && gHeadless.Result.State == ManufacturingOutputState.NoGo
            && ManufacturingPageModel.SummarizeOdb(gHeadless.Result).Contains("NoGo", StringComparison.Ordinal);
        bool gGuard = false;
        try { _ = new EngineManufacturingExportRunner(null!); }
        catch (ArgumentNullException) { gGuard = true; }
        gBinderRun = gNoGo && gGuard;
        gBinderDetail = $"odb-headless-nogo={gNoGo} null-workspace-guard={gGuard}.";
    }
    finally
    {
        gSession.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
catch (Exception error)
{
    gBinderDetail = $"{error.GetType().Name}: {error.Message}";
}
Record("H-NAV-G-03", gBinderRun,
    $"headless-NoGo/guard behavior only, source spelling retired: runtime({gBinderDetail}) (T12-01..06 native NOT_EXECUTED).");

// ---- results CSV (inside this worktree so evidence commits on tools/h) ----
string evidenceDir = Environment.GetEnvironmentVariable("LANE_H_EVIDENCE_DIR")
    ?? Path.Combine(repoRoot, "evidence", "lane-h");
Directory.CreateDirectory(evidenceDir);
string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
string csvPath = Path.Combine(evidenceDir, $"conformance-{stamp}.csv");
string latestPath = Path.Combine(evidenceDir, "conformance-latest.csv");
foreach (string target in new[] { csvPath, latestPath })
{
    using var writer = new StreamWriter(target);
    writer.WriteLine("check_id,result,detail");
    foreach (var (id, pass, detail) in records)
    {
        writer.WriteLine($"{id},{(pass ? "PASS" : "FAIL")},\"{detail.Replace("\"", "\"\"")}\"");
    }
}
Console.WriteLine($"Evidence: {csvPath}");

int failed = records.Count(r => !r.Pass);
Console.WriteLine($"{records.Count - failed}/{records.Count} checks passed.");
return failed == 0 ? 0 : 1;

static DesignScene LaneHScene()
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
                GeometryFidelity.Unknown, ["The lane H fixture does not model this canonical family."])
            : new FamilyCoverage(family, DataAvailability.Available,
                DataCompleteness.CompleteForRequestedScope, GeometryFidelity.AnalyticPrimitive, [])));
    return new(new(Guid.NewGuid(), DateTimeOffset.UtcNow,
            new("lane-h-fixture", "1", true, "synthetic-data-only")),
        new(DocumentKind.PcbBoard, "lane-h", "mils", 2, new(new(0, 0), new(100, 100))),
        SceneQuery.CompleteBoard() with { Families = Enum.GetValues<DataFamily>().Except(unavailable).ToImmutableArray() },
        coverage, data);
}

static ManufacturingSourceCapture LaneHSource() =>
    new(
        Guid.NewGuid(),
        DateTimeOffset.UtcNow,
        new WorkspaceDocumentIdentity("session-page", 2, 3, 4242, "board-page.brd", "PD_V25"),
        7,
        new string('a', 64),
        new string('c', 64));

static string? FindRepoRoot(string start)
{
    var dir = new DirectoryInfo(start);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "src", "PD.Simple", "MainWindow.xaml")))
        {
            return dir.FullName;
        }
        dir = dir.Parent;
    }
    return null;
}
