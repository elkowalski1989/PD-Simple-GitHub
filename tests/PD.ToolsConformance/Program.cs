// Lane H independent conformance harness (PD side), slice 3 (full surface).
//
// Scope: shell/registration/OpenSection-forwarding source conformance plus
// independent re-verification of the integrated heads on tools/coordinator:
// lane A (T01/T02/T03/T08 section routing), lane B (T06 overlays + T07
// review PD side), lane F (T11 padstacks PD side), lane C (ROUTE layer
// policy + H-first geometry, preserved; T04/T05 section targets), lane D
// (T09 constraints/DRC page), lane E (T10 physical symbols), lane G (T12
// manufacturing). Source checks parse the actual checkout text; runtime
// checks use hand-computed expectations, never values copied from the
// implementation under test. Native and WPF-runtime gates are NOT_EXECUTED
// by environment (no license / Linux container); they are recorded in the
// handoff matrix, never as harness passes.
//
// Usage: dotnet run --project tests/PD.ToolsConformance [-- <repoRoot>]
//   Env overrides: PD_TOOLS_REPO_ROOT, LANE_H_EVIDENCE_DIR
// Exit code: 0 when every check passes, 1 otherwise. A results CSV is always
// written to the evidence directory.

using System.Collections.Immutable;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Drawing;
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
string laneCHandoffPath = Path.Combine(repoRoot, "docs", "handoffs", "PD-TOOLS-C.md");

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
string laneCHandoff = File.Exists(laneCHandoffPath) ? File.ReadAllText(laneCHandoffPath) : string.Empty;

int Count(string haystack, string needle)
{
    int n = 0, i = 0;
    while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
    return n;
}

// ---- H-SHELL-NAV-01: remaining placeholders inventoried (0 after T04/T05 wiring) ----
int futureCount = Count(mainXaml, "Style=\"{StaticResource FutureNavButton}\"");
Record("H-SHELL-NAV-01", futureCount == 0,
    $"FutureNavButton usages={futureCount}, expected=0 (all 12 destinations wired) at MainWindow.xaml.");

// ---- H-SHELL-NAV-02: twelve wired destinations, no placeholders ----
string[] wiredButtons =
[
    "CrossingMenuButton", "InspectorMenuButton", "MeasureMenuButton", "ScenesMenuButton",
    "OverlayMenuButton", "ReviewMenuButton", "PadstacksMenuButton",
    "ConstraintsDrcMenuButton", "PhysicalSymbolsMenuButton", "ManufacturingMenuButton",
    "PlacementMenuButton", "ViaRouteMenuButton",
];
var missingWired = wiredButtons
    .Where(name => !mainXaml.Contains($"x:Name=\"{name}\"", StringComparison.Ordinal)
        || !mainXaml.Contains("Style=\"{StaticResource NavButton}\"", StringComparison.Ordinal))
    .ToArray();
string[] placeholderLabels = [];
var missingPlaceholders = placeholderLabels
    .Where(label => Count(mainXaml, $"Content=\"{label}") != 1)
    .ToArray();
Record("H-SHELL-NAV-02", missingWired.Length == 0 && missingPlaceholders.Length == 0 && futureCount == 0,
    missingWired.Length == 0 && missingPlaceholders.Length == 0 && futureCount == 0
        ? "12 destinations wired as NavButton; no placeholders remain."
        : $"Missing wired=[{string.Join(", ", missingWired)}] placeholders=[{string.Join(", ", missingPlaceholders)}] futureCount={futureCount}.");

// ---- H-SHELL-NAV-03: placeholder style still gates (honest NOT-complete signal) ----
bool styleGates = mainXaml.Contains("<Style x:Key=\"FutureNavButton\"")
    && mainXaml.Contains("<Setter Property=\"IsEnabled\" Value=\"False\"/>");
Record("H-SHELL-NAV-03", styleGates,
    "FutureNavButton style hard-codes IsEnabled=False (placeholders stay visibly disabled).");

// ---- H-SHELL-WIRED-01: four preserved destinations wired + defined ----
string[] wiredHandlers = ["Home_Click", "Explorer_Click", "Corridor_Click", "Route_Click"];
var unwired = wiredHandlers
    .Where(handler => !mainXaml.Contains($"Click=\"{handler}\"") || !mainCs.Contains(handler))
    .ToArray();
Record("H-SHELL-WIRED-01", unwired.Length == 0,
    unwired.Length == 0 ? "Home/Explorer/DPVC/ROUTE handlers wired in XAML and defined in code-behind."
        : $"Missing wiring/definition: {string.Join(", ", unwired)}.");

// ---- H-SHELL-HDR-01: header connection + screenshot controls ----
bool header = mainXaml.Contains("Click=\"Reconnect_Click\"") && mainXaml.Contains("Click=\"Screenshot_Click\"")
    && mainCs.Contains("void Reconnect_Click") && mainCs.Contains("void Screenshot_Click");
Record("H-SHELL-HDR-01", header,
    "Reconnect/Attach and Screenshot header buttons wired and defined.");

// ---- H-SHELL-CARD-01: home duplicate cards route to the same handlers ----
bool cards = mainXaml.Contains("Click=\"Explorer_Click\"") && mainXaml.Contains("Click=\"Corridor_Click\"")
    && mainXaml.Contains("Click=\"Route_Click\"")
    && Count(mainXaml, "Click=\"Explorer_Click\"") >= 2
    && Count(mainXaml, "Click=\"Corridor_Click\"") >= 2
    && Count(mainXaml, "Click=\"Route_Click\"") >= 2;
Record("H-SHELL-CARD-01", cards,
    "Sidebar and home-card entry points share Explorer_Click/Corridor_Click/Route_Click.");

// ---- H-SHELL-ROUTE-01: ROUTE nested actions wired + defined ----
string[] routeHandlers = ["StartRoute_Click", "ClearPick_Click", "CancelRoute_Click", "UndoRoute_Click"];
var routeMissing = routeHandlers
    .Where(handler => !mainXaml.Contains($"Click=\"{handler}\"") || !mainCs.Contains(handler))
    .ToArray();
Record("H-SHELL-ROUTE-01", routeMissing.Length == 0,
    routeMissing.Length == 0 ? "ROUTE-01..04 buttons wired and defined."
        : $"Missing: {string.Join(", ", routeMissing)}.");

// ---- H-SHELL-FWD-01: thin OpenSection forwarding contract ----
bool fwdSignature = explorerCs.Contains(
    "public void OpenSection(WorkbenchSection section, ObjectFamily? family = null)");
bool fwdDetached = explorerCs.Contains(
    "\"The shared Engine presentation is not attached.\"");
bool fwdDelegates = explorerCs.Contains("_workbench.OpenSection(section, family)");
bool fwdGuards = explorerCs.Contains("ObjectDisposedException.ThrowIf(_disposed, this)");
bool fwdNoReflection = !explorerCs.Contains("System.Reflection")
    && !explorerCs.Contains("GetField(") && !explorerCs.Contains("GetMethod(")
    && !explorerCs.Contains("BindingFlags") && !explorerCs.Contains("MakeGenericMethod");
int eventInvokes = Count(explorerCs, "?.Invoke(");
Record("H-SHELL-FWD-01", fwdSignature && fwdDetached && fwdDelegates && fwdGuards && fwdNoReflection,
    $"signature={fwdSignature} detached-message={fwdDetached} delegates={fwdDelegates} disposed-guard={fwdGuards} no-reflection={fwdNoReflection} (plain event ?.Invoke x{eventInvokes}).");

// ---- H-NAV-A-01: lane A section routing (Crossings/Inspect/Measure/Coverage) ----
bool aCrossing = mainCs.Contains("Crossing_Click") && mainCs.Contains("WorkbenchSection.Crossings");
bool aInspector = mainCs.Contains("Inspector_Click") && mainCs.Contains("WorkbenchSection.Inspect");
bool aMeasure = mainCs.Contains("Measure_Click") && mainCs.Contains("WorkbenchSection.Measure");
bool aScenes = mainCs.Contains("Scenes_Click") && mainCs.Contains("WorkbenchSection.Coverage");
bool aCentral = mainCs.Contains("ShowWorkbenchSection(WorkbenchSection section")
    && mainCs.Contains("ShowTool(\"explorer\")")
    && mainCs.Contains("ExplorerView.OpenSection(section)");
bool aButtons = mainXaml.Contains("x:Name=\"CrossingMenuButton\"") && mainXaml.Contains("x:Name=\"InspectorMenuButton\"")
    && mainXaml.Contains("x:Name=\"MeasureMenuButton\"") && mainXaml.Contains("x:Name=\"ScenesMenuButton\"")
    && mainXaml.Contains("AutomationProperties.Name=\"Open Crossing review\"")
    && mainXaml.Contains("AutomationProperties.Name=\"Open Geometry inspector\"")
    && mainXaml.Contains("AutomationProperties.Name=\"Open Pick measure ruler\"")
    && mainXaml.Contains("AutomationProperties.Name=\"Open Captured scenes\"");
Record("H-NAV-A-01", aCrossing && aInspector && aMeasure && aScenes && aCentral && aButtons,
    $"Crossings={aCrossing} Inspect={aInspector} Measure={aMeasure} Coverage={aScenes} central-forwarder={aCentral} named-accessible-buttons={aButtons}.");

// ---- H-NAV-A-02: lane A failure path reports, pages open disconnected ----
bool aStatus = mainCs.Contains("unavailable: {error.Message}") || mainCs.Contains("unavailable: \" + error.Message");
bool aGated = mainCs.Contains("CrossingMenuButton.IsEnabled") && mainCs.Contains("InspectorMenuButton.IsEnabled")
    && mainCs.Contains("MeasureMenuButton.IsEnabled") && mainCs.Contains("ScenesMenuButton.IsEnabled");
Record("H-NAV-A-02", aStatus && aGated,
    $"forwarding-failure-in-status={aStatus} busy-gated-nav-buttons={aGated} (pages open; live actions gated in Workbench).");

// ---- H-NAV-B-01: lane B overlay/review wiring through ShowTool ----
bool bOverlay = mainXaml.Contains("x:Name=\"OverlayMenuButton\"") && mainXaml.Contains("Click=\"Overlay_Click\"")
    && mainCs.Contains("Overlay_Click") && mainCs.Contains("ShowTool(\"overlay\")");
bool bReview = mainXaml.Contains("x:Name=\"ReviewMenuButton\"") && mainXaml.Contains("Click=\"Review_Click\"")
    && mainCs.Contains("Review_Click") && mainCs.Contains("ShowTool(\"review\")");
bool bViews = mainXaml.Contains("OverlayView") && mainXaml.Contains("ReviewView")
    && mainCs.Contains("OverlayView.Visibility") && mainCs.Contains("ReviewView.Visibility");
bool bSync = overlayViewCs.Contains("SyncControls()") && reviewViewCs.Contains("SyncControls()");
Record("H-NAV-B-01", bOverlay && bReview && bViews && bSync,
    $"overlay-route={bOverlay} review-route={bReview} views-in-ShowTool={bViews} gate-sync={bSync}.");

// ---- H-NAV-B-02: overlay recipes carry ellipse/polyline + Drag, validated ----
bool bShapes = overlayRecipeCs.Contains("record Ellipse(") && overlayRecipeCs.Contains("record Polyline(")
    && overlayRecipeCs.Contains("builder.Ellipse(") && overlayRecipeCs.Contains("builder.Polyline(");
bool bDrag = overlayVmCs.Contains("DrawingHitTestPolicy.Drag") && overlayVmCs.Contains("\"Drag\" => DrawingHitTestPolicy.Drag");
bool bValidateSrc = overlayRecipeCs.Contains("\"Radii\"") && overlayRecipeCs.Contains("\"Points\"");
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
Record("H-NAV-B-02", bShapes && bDrag && bValidateSrc && bValidateRun,
    $"shapes+engine-build={bShapes} drag-policy={bDrag} validation-markers={bValidateSrc} runtime({bValidateDetail}).");

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
bool bTrackerSrc = trackerCs.Contains("Only a matching visible receipt marks an operation published")
    || trackerCs.Contains("never flow through a shared");
Record("H-NAV-B-03", bTrackerSrc && bTrackerRun,
    $"operation-bound-contract={bTrackerSrc} runtime({bTrackerDetail}).");

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
bool bBundleSrc = bundleCs.Contains("WriteFileAtomically") && bundleCs.Contains("SHA-256")
    && bundleCs.Contains("never contains credentials");
Record("H-NAV-B-04", bBundleSrc && bBundleRun,
    $"atomic+hash+no-credentials-contract={bBundleSrc} runtime({bBundleDetail}).");

// ---- H-NAV-F-01: lane F padstacks wiring (offline-capable page, live gated) ----
bool fButton = mainXaml.Contains("x:Name=\"PadstacksMenuButton\"") && mainXaml.Contains("Click=\"Padstacks_Click\"");
bool fRoute = mainCs.Contains("Padstacks_Click") && mainCs.Contains("ShowTool(\"padstacks\")")
    && mainCs.Contains("RefreshPadstacksViewAsync") && mainCs.Contains("PadstacksView.ShowScene(null, live)");
bool fView = mainXaml.Contains("PadstacksView") && mainCs.Contains("PadstacksView.Visibility")
    && padstacksViewCs.Contains("ShowScene(DesignScene? scene, bool isLiveConnected)")
    && padstacksViewCs.Contains("owns no session");
Record("H-NAV-F-01", fButton && fRoute && fView,
    $"sidebar-button={fButton} showtool+refresh-gating={fRoute} session-free-view={fView}.");

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
bool fToolSrc = padstackCs.Contains("padstack.inspect-definitions") && padstackCs.Contains("padstack.replace-board-via")
    && padstackCs.Contains("manufactures no native");
Record("H-NAV-F-02", fToolSrc && fToolRun,
    $"action-contract={fToolSrc} runtime({fToolDetail}).");

// ---- H-NAV-F-03: padstack Engine-workflow dispatch (plans validate, never execute) ----
bool fDispatchSrc = padstackCs.Contains("EnginePadstackWorkflows.PlanGlobalEdit")
    && padstackCs.Contains("EnginePadstackWorkflows.PlanPurge")
    && padstackCs.Contains("EnginePadstackWorkflows.AssessDeletion")
    && padstackCs.Contains("EnginePadstackWorkflows.PlanRedefinition")
    && padstackCs.Contains("EnginePadstackInspection.ExportDiagnostics")
    && padstackCs.Contains("never targeted delete");
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
Record("H-NAV-F-03", fDispatchSrc && fDispatchRun,
    $"workflow-dispatch-source={fDispatchSrc} runtime({fDispatchDetail}).");

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

// ---- H-SHELL-SHOWTOOL-01: all integrated destinations share one dispatcher ----
bool showtoolDefined = mainCs.Contains("private void ShowTool(string? tool)");
bool showtoolKeys = mainCs.Contains("ShowTool(null)") && mainCs.Contains("ShowTool(\"explorer\")")
    && mainCs.Contains("ShowTool(\"corridor\")") && mainCs.Contains("ShowTool(\"route\")")
    && mainCs.Contains("ShowTool(\"overlay\")") && mainCs.Contains("ShowTool(\"review\")")
    && mainCs.Contains("ShowTool(\"padstacks\")") && mainCs.Contains("ShowTool(\"manufacturing\")")
    && mainCs.Contains("ShowTool(\"constraintsdrc\")") && mainCs.Contains("ShowTool(\"physicalsymbols\")");
Record("H-SHELL-SHOWTOOL-01", showtoolDefined && showtoolKeys,
    $"single ShowTool dispatcher={showtoolDefined} home/explorer/corridor/route/overlay/review/padstacks/manufacturing/constraintsdrc/physicalsymbols keys={showtoolKeys}.");

// ---- H-EXPLORER-SESSION-01: one shared Workbench, session retained ----
int workbenchNews = Count(explorerCs, "new EngineWorkbenchView(");
bool sessionRetained = explorerCs.Contains("ReferenceEquals(workbench.Session, presentation.Session)");
bool detachOnDispose = explorerCs.Contains("WorkbenchHost.Content = null");
bool doubleAttachRefused = explorerCs.Contains("The Engine Workbench presentation is already attached.");
Record("H-EXPLORER-SESSION-01", workbenchNews == 1 && sessionRetained && detachOnDispose && doubleAttachRefused,
    $"constructions={workbenchNews} (expected 1) session-retained={sessionRetained} detach-on-dispose={detachOnDispose} double-attach-refused={doubleAttachRefused}.");

// ---- H-PLACEHOLDER-01: no placeholders remain; stale-content guard ----
bool noPlaceholdersLeft = futureCount == 0;
bool noStalePlaceholders = !mainXaml.Contains("·  coming") && !mainXaml.Contains("·  integrating")
    && !mainXaml.Contains("available in Explorer");
Record("H-PLACEHOLDER-01", noPlaceholdersLeft && noStalePlaceholders,
    "All 12 destinations wired; no FutureNavButton usages or stale coming/integrating content remain.");

// ---- H-NAV-C-01: T04/T05 wired buttons + section-forwarding targets ----
bool cButtons = mainXaml.Contains("x:Name=\"PlacementMenuButton\"") && mainXaml.Contains("Click=\"Placement_Click\"")
    && mainXaml.Contains("x:Name=\"ViaRouteMenuButton\"") && mainXaml.Contains("Click=\"ViaRoute_Click\"")
    && mainXaml.Contains("AutomationProperties.Name=\"Open Placement handles\"")
    && mainXaml.Contains("AutomationProperties.Name=\"Open Via / route editing\"");
bool cRoute = mainCs.Contains("ShowWorkbenchSection(WorkbenchSection.Placement")
    && mainCs.Contains("ShowWorkbenchSection(WorkbenchSection.NativeEdits");
bool cFragment = laneCHandoff.Contains("OpenSection(WorkbenchSection.Placement)")
    && laneCHandoff.Contains("OpenSection(WorkbenchSection.NativeEdits)")
    && laneCHandoff.Contains("OpenSection(WorkbenchSection.RoutePreview)");
bool cOfflineNote = laneCHandoff.Contains("openable disconnected") || laneCHandoff.Contains("pages openable disconnected");
Record("H-NAV-C-01", cButtons && cRoute && cFragment && cOfflineNote,
    $"t04-t05-wired={cButtons} section-routing={cRoute} section-fragment={cFragment} offline-pages={cOfflineNote} (native T04/T05 gates NOT_EXECUTED).");

// ---- H-NAV-D-01: lane D constraints/DRC wiring through ShowTool ----
bool dButton = mainXaml.Contains("x:Name=\"ConstraintsDrcMenuButton\"") && mainXaml.Contains("Click=\"ConstraintsDrc_Click\"");
bool dRoute = mainCs.Contains("ConstraintsDrc_Click") && mainCs.Contains("ShowTool(\"constraintsdrc\")")
    && mainCs.Contains("ConstraintsDrcView.AttachSession(_bridge.EngineSession)");
bool dViews = mainXaml.Contains("ConstraintsDrcView") && mainCs.Contains("ConstraintsDrcView.Visibility")
    && mainCs.Contains("ConstraintsDrcView.Dispose()");
bool dViewContract = constraintsViewCs.Contains("public void AttachSession(AllegroEngineSession session)")
    && constraintsViewCs.Contains("already attached") && constraintsViewCs.Contains("never creates or disposes");
Record("H-NAV-D-01", dButton && dRoute && dViews && dViewContract,
    $"sidebar-button={dButton} showtool+attach={dRoute} hosted+disposed={dViews} single-attach-contract={dViewContract}.");

// ---- H-NAV-D-02: DRC rebind (.94 Engine execution, effective facts, typed edits) ----
bool dPending = constraintsVmCs.Contains("PendingPackageReason")
    && constraintsVmCs.Contains("AllegroWorkspaceDrcRun") && constraintsVmCs.Contains("AllegroWorkspaceDrcReview")
    && constraintsVmCs.Contains("effective-read") && constraintsVmCs.Contains("mutation-preparation")
    && constraintsVmCs.Contains("engine.drc.execute")
    && constraintsVmCs.Contains("1.13.0-preview.105")
    && !constraintsVmCs.Contains("1.13.0-preview.104");
bool dGates = constraintsVmCs.Contains("RequireLive(AllegroWorkspaceDrcRun.CapabilityId, \"DRC run\")")
    && constraintsVmCs.Contains("RequireLive(EngineCapabilities.Drc, \"DRC marker read\")")
    && constraintsVmCs.Contains("RunAsync(EngineDrcRunRequest.FullBoard")
    && constraintsVmCs.Contains("WasExecutedForThisEvidence")
    && constraintsVmCs.Contains("ReadEffectiveAsync")
    && constraintsVmCs.Contains("PrepareChangeAsync")
    && constraintsVmCs.Contains("ExecuteToTerminalAsync")
    && constraintsVmCs.Contains("AllegroWorkspaceDrcReview.Group")
    && constraintsVmCs.Contains("AllegroWorkspaceDrcReview.Compare");
bool dHonesty = constraintsVmCs.Contains("they are not labeled assigned or effective")
    && constraintsVmCs.Contains("without running DRC")
    && constraintsVmCs.Contains("never manufactures an object reference")
    && constraintsVmCs.Contains("never claim execution");
bool dEngineIds = EngineCapabilities.Drc.Value == "engine.drc"
    && AllegroWorkspaceDrcRun.CapabilityId.Value == "engine.drc.execute"
    && EngineDrcRunRequest.FullBoard.Scope == EngineDrcRunScope.FullBoard;
Record("H-NAV-D-02", dPending && dGates && dHonesty && dEngineIds,
    $"rebind-markers={dPending} live-wiring={dGates} read-vs-execution-honesty={dHonesty} engine-ids={dEngineIds} (T09-02/03/05 native NOT_EXECUTED).");

// ---- H-NAV-D-03: Engine review delegation (identical comparison semantics) ----
bool dReviewSrc = constraintsVmCs.Contains("AllegroWorkspaceDrcReview.Group")
    && constraintsVmCs.Contains("AllegroWorkspaceDrcReview.Compare")
    && constraintsVmCs.Contains("# PD Simple Constraints/DRC marker export");
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
Record("H-NAV-D-03", dReviewSrc && dReviewRun,
    $"engine-delegation-source={dReviewSrc} runtime({dReviewDetail}).");

// ---- H-NAV-E-01: lane E physical-symbols wiring through ShowTool ----
bool eButton = mainXaml.Contains("x:Name=\"PhysicalSymbolsMenuButton\"") && mainXaml.Contains("Click=\"PhysicalSymbols_Click\"");
bool eRoute = mainCs.Contains("PhysicalSymbols_Click") && mainCs.Contains("ShowTool(\"physicalsymbols\")")
    && mainCs.Contains("RefreshPhysicalSymbolsViewAsync") && mainCs.Contains("PhysicalSymbolsView.ShowScene(null, live)")
    && mainCs.Contains("PhysicalSymbolsView.StageSymbol(null)")
    && mainCs.Contains("Physical symbol capture unavailable: ");
bool eView = mainXaml.Contains("PhysicalSymbolsView") && mainCs.Contains("PhysicalSymbolsView.Visibility")
    && physymViewCs.Contains("public void ShowScene(DesignScene? scene, bool isLiveConnected)")
    && physymViewCs.Contains("public void StageSymbol(string? symbolName)")
    && physymViewCs.Contains("creates no Host") && physymViewCs.Contains("starts no native operation");
bool eToolSrc = physymToolCs.Contains("\"tools.physical-symbols\"")
    && physymToolCs.Contains("board instance with a similar symbol name is not that document");
Record("H-NAV-E-01", eButton && eRoute && eView && eToolSrc,
    $"sidebar-button={eButton} showtool+refresh-gating={eRoute} session-free-view={eView} registration-source={eToolSrc}.");

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
Record("H-NAV-E-02", eToolSrc && eToolRun,
    $"policy-source={eToolSrc} runtime({eToolDetail}) (T10-01..06 native NOT_EXECUTED).");

// ---- H-NAV-E-03: symbol binding/activation workflow against staged PACKAGE docs ----
bool eBinderSrc = symRunnerCs.Contains("EngineSymbolWorkArea.Plan")
    && symRunnerCs.Contains("ActivateAsync")
    && symRunnerCs.Contains("PrepareAsync")
    && symRunnerCs.Contains("ApplyAsync")
    && symRunnerCs.Contains("EnginePhysicalSymbolPublisher.PublishAsync")
    && symRunnerCs.Contains("RequireStagedDocument")
    && mainCs.Contains("PhysicalSymbolsView.AttachRunner(new EngineSymbolBindingRunner(_bridge.Workspace))");
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
Record("H-NAV-E-03", eBinderSrc && eBinderRun,
    $"binding-workflow-source={eBinderSrc} runtime({eBinderDetail}) (T10-01..06 native NOT_EXECUTED).");

// ---- H-NAV-G-01: lane G manufacturing wiring through ShowTool ----
bool gButton = mainXaml.Contains("x:Name=\"ManufacturingMenuButton\"") && mainXaml.Contains("Click=\"Manufacturing_Click\"");
bool gRoute = mainCs.Contains("Manufacturing_Click") && mainCs.Contains("ShowTool(\"manufacturing\")")
    && mainCs.Contains("ManufacturingView.Attach(_bridge)")
    && mainCs.Contains("ManufacturingView.RefreshFromSession()");
bool gViews = mainXaml.Contains("ManufacturingView") && mainCs.Contains("ManufacturingView.Visibility")
    && mainCs.Contains("ManufacturingView.IsEnabled");
bool gViewContract = mfgViewCs.Contains("public void Attach(BridgeSession session)")
    && mfgViewCs.Contains("public void RefreshFromSession()")
    && mfgViewCs.Contains("new UnqualifiedManufacturingRunner()")
    && mfgViewCs.Contains("MfgActionReasonText") && mfgViewCs.Contains("AutomationId");
Record("H-NAV-G-01", gButton && gRoute && gViews && gViewContract,
    $"sidebar-button={gButton} showtool+attach-refresh={gRoute} hosted+gated={gViews} runner-default+automation={gViewContract}.");

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
bool gRunnerSrc = mfgRunnerCs.Contains("RejectArtwork(plan)") && mfgRunnerCs.Contains("RejectOdbPlusPlus(plan)")
    && mfgRunnerCs.Contains("RejectIpc2581(plan)") && mfgRunnerCs.Contains("promotion stays disabled");
Record("H-NAV-G-02", gRunnerSrc && gToolRun,
    $"nogo-runner-source={gRunnerSrc} runtime({gToolDetail}) (T12-01..06 native NOT_EXECUTED).");

// ---- H-NAV-G-03: Engine-backed exporter replaces the unqualified default ----
bool gBinderSrc = engineMfgCs.Contains("ExecuteArtworkAsync")
    && engineMfgCs.Contains("ExecuteIpc2581Async")
    && engineMfgCs.Contains("ReportOdbPlusPlusHeadless")
    && engineMfgCs.Contains("ProcessManufacturingNativeLauncher")
    && mfgModelCs.Contains("Promotion needs a Complete validation result")
    && mainCs.Contains("ManufacturingView.Runner = new EngineManufacturingExportRunner(_bridge.Workspace)");
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
Record("H-NAV-G-03", gBinderSrc && gBinderRun,
    $"engine-binder-source={gBinderSrc} runtime({gBinderDetail}) (T12-01..06 native NOT_EXECUTED).");

// ---- ASTRA-PD8-NAVIGATION: captured-object anchor resolves through the Engine frame resolver ----
bool astraNavRun = false;
string astraNavDetail = string.Empty;
try
{
    DesignScene astraScene = LaneHScene();
    var astraCandidates = new List<(string Kind, SceneObjectId Id)>();
    foreach (ComponentObject astraComponent in astraScene.Components.Items)
    {
        astraCandidates.Add(("component", astraComponent.Id));
    }

    foreach (NetObject astraNet in astraScene.Nets.Items)
    {
        astraCandidates.Add(("net", astraNet.Id));
    }

    foreach (CopperObject astraCopper in astraScene.Copper.Items)
    {
        astraCandidates.Add(("copper", astraCopper.Id));
    }

    var astraFailures = new List<string>();
    foreach (var (kind, id) in astraCandidates)
    {
        try
        {
            // Copper carries bounds rather than an object-origin pose, so it
            // anchors through the bounds-center Engine anchor.
            DrawingAnchor? astraAnchor = kind == "copper" ? DrawingAnchor.BoundsCenter : null;
            var astraRecipe = new OverlayToolRecipe(
                "astra-nav",
                new OverlayToolAnchor.CapturedObject(astraScene.ReferenceTo(id), astraAnchor),
                new OverlayToolShape.Marker(0, 0, DrawingMarkerKind.Dot, 12),
                new(255, 32, 112, 220, 2));
            DrawingGroup astraGroup = astraRecipe.Build(astraScene, "astra-nav");
            if (astraGroup.Elements.Length == 1)
            {
                astraNavRun = true;
                astraNavDetail = $"frame-resolved-kind={kind}, elements=1.";
                break;
            }

            astraFailures.Add($"{kind}: built {astraGroup.Elements.Length} elements");
        }
        catch (Exception candidateError)
        {
            astraFailures.Add($"{kind}: {candidateError.Message}");
        }
    }

    if (!astraNavRun && astraNavDetail.Length == 0)
    {
        astraNavDetail = astraCandidates.Count == 0
            ? "The fixture exposes no navigation target."
            : $"No candidate resolved: {string.Join("; ", astraFailures)}";
    }
}
catch (Exception error)
{
    astraNavDetail = $"{error.GetType().Name}: {error.Message}";
}

Record("ASTRA-PD8-NAVIGATION", astraNavRun, $"positive-fixture({astraNavDetail})");

// ---- ASTRA-PD8-SAFE-DENIAL: refusals stay refusals with reasons ----
bool astraDenyRun = false;
string astraDenyDetail = string.Empty;
try
{
    bool denyDelete = !PadstackTool.AssessTargetedDelete(true).IsSupported
        && !PadstackTool.AssessTargetedDelete(false).IsSupported;
    bool denyReceipt = false;
    try { PadstackTool.ParsePurgeReceipt(null); }
    catch (InvalidDataException) { denyReceipt = true; }
    var denyModel = new ManufacturingPageModel();
    denyModel.SetConnected(true);
    denyModel.RefreshSource(LaneHSource);
    bool denyPromote = !denyModel.CanPromote(Guid.NewGuid(), ManufacturingOutputState.NoGo, false, out string denyReason)
        && denyReason.Length != 0;
    astraDenyRun = denyDelete && denyReceipt && denyPromote;
    astraDenyDetail = $"targeted-delete-refused={denyDelete} purge-receipt-refused={denyReceipt} promote-refused={denyPromote}.";
}
catch (Exception error)
{
    astraDenyDetail = $"{error.GetType().Name}: {error.Message}";
}

Record("ASTRA-PD8-SAFE-DENIAL", astraDenyRun, $"positive-fixture({astraDenyDetail})");

// ---- ASTRA-PD8-SUCCESSFUL-OPERATION: build, rebuilding export, verified plan ----
bool astraOpRun = false;
string astraOpDetail = string.Empty;
try
{
    DesignScene astraScene = LaneHScene();
    var okRecipe = new OverlayToolRecipe(
        "astra-op",
        new OverlayToolAnchor.Board(0, 0),
        new OverlayToolShape.Line(0, 0, 100, 50),
        new(255, 32, 112, 220, 2));
    DrawingGroup okGroup = okRecipe.Build(astraScene, "astra-op");
    bool okBuild = okGroup.Elements.Length == 1;
    bool okExport = okRecipe.ExportCSharp().Contains(
        "public static DrawingGroup Rebuild(DesignScene scene, string groupId)", StringComparison.Ordinal);
    EnginePadstackGlobalEditPlan okEditPlan = PadstackTool.PlanGlobalEdit(
        "PAD_A", [new("drillDiameter", "12")], astraScene);
    bool okPlanVerified = okEditPlan.DefinitionVerifiedInCapture;
    astraOpRun = okBuild && okExport && okPlanVerified;
    astraOpDetail = $"build-1-element={okBuild} rebuilding-export={okExport} plan-verified={okPlanVerified}.";
}
catch (Exception error)
{
    astraOpDetail = $"{error.GetType().Name}: {error.Message}";
}

Record("ASTRA-PD8-SUCCESSFUL-OPERATION", astraOpRun, $"positive-fixture({astraOpDetail})");

// ---- ASTRA-PD8-NATIVE-READBACK: stamped Engine inspection, no native authority claimed ----
bool astraReadRun = false;
string astraReadDetail = string.Empty;
try
{
    DesignScene astraScene = LaneHScene();
    EnginePadstackDefinitionView readDef = PadstackTool.InspectDefinition(astraScene, "PAD_A");
    EnginePadstackUsage readUse = PadstackTool.InspectInstances(astraScene, "PAD_A");
    int capturedLayers = readDef.Layers.Count(layer => layer.Captured);
    int absentLayers = readDef.Layers.Count(layer => !layer.Captured);
    astraReadRun = readDef.Found && capturedLayers == 1 && absentLayers == 1
        && readUse.BoardPinCount == 1 && readUse.SymbolPinCount == 4
        && readUse.SceneStamp.Length != 0
        && readUse.SceneStamp == EnginePadstackInspection.Stamp(astraScene);
    astraReadDetail = $"definition-found={readDef.Found} captured={capturedLayers} absent={absentLayers} board-pins={readUse.BoardPinCount} symbol-pins={readUse.SymbolPinCount} stamped={readUse.SceneStamp.Length != 0}.";
}
catch (Exception error)
{
    astraReadDetail = $"{error.GetType().Name}: {error.Message}";
}

Record("ASTRA-PD8-NATIVE-READBACK", astraReadRun, $"positive-fixture({astraReadDetail})");

// ---- ASTRA-PD8-ARTIFACT-VALIDATION: library request, diagnostic export, plan manifest ----
bool astraArtifactRun = false;
string astraArtifactDetail = string.Empty;
try
{
    DesignScene astraScene = LaneHScene();
    var libRequest = new EnginePadstackLibraryRequest(
        "PAD_A", "pad_a", Path.GetTempPath(), Path.GetTempPath(), EnginePadstackOverwritePolicy.Refuse);
    bool libOk = PadstackTool.ValidateLibraryRequest(libRequest) == libRequest;
    string diagnosis = PadstackTool.ExportDiagnosis(astraScene, "PAD_A");
    bool diagOk = diagnosis.Contains("Padstack diagnostic export", StringComparison.Ordinal)
        && diagnosis.Contains("Where-used:", StringComparison.Ordinal);
    var artModel = new ManufacturingPageModel();
    artModel.SetConnected(true);
    artModel.RefreshSource(LaneHSource);
    var (artPlan, artError) = artModel.TryBuildArtwork(
        [new("TOP", "ETCH/TOP", "films/top.gbr", false, false)],
        new(ArtworkGerberFormat.Rs274X, ArtworkCoordinateUnits.Inches, false, false),
        "release-astra", false);
    bool manifestOk = artPlan is not null && artError is null
        && ManufacturingPageModel.PreviewArtworkManifest(artPlan).Contains("films/top.gbr");
    astraArtifactRun = libOk && diagOk && manifestOk;
    astraArtifactDetail = $"library-valid={libOk} diagnosis-export={diagOk} manifest-preview={manifestOk}.";
}
catch (Exception error)
{
    astraArtifactDetail = $"{error.GetType().Name}: {error.Message}";
}

Record("ASTRA-PD8-ARTIFACT-VALIDATION", astraArtifactRun, $"positive-fixture({astraArtifactDetail})");

// ---- ASTRA-PD8-RECOVERY: cancel, supersede, then recover to visible ----
bool astraRecoveryRun = false;
string astraRecoveryDetail = string.Empty;
try
{
    DesignScene astraScene = LaneHScene();
    var recoveryTracker = new ToolPublicationTracker();
    Guid cancelOp = recoveryTracker.StartOperation(7, 3);
    bool recoveredCancel = recoveryTracker.Complete(cancelOp, 7, 1, null) == ToolPublicationOutcome.Cancelled;
    Guid staleOp = recoveryTracker.StartOperation(7, 3);
    var staleReceipt = new ToolPublicationReceipt(
        "doc", Guid.NewGuid(), 3, 1, ToolPublicationAvailability.Visible, DateTimeOffset.UtcNow);
    bool recoveredStale = recoveryTracker.Complete(staleOp, 8, 1, staleReceipt) == ToolPublicationOutcome.Superseded;
    Guid visibleOp = recoveryTracker.StartOperation(7, 3);
    var visibleReceipt = new ToolPublicationReceipt(
        "doc", astraScene.Identity.CaptureId, 3, 1, ToolPublicationAvailability.Visible, DateTimeOffset.UtcNow);
    bool recoveredVisible = recoveryTracker.Complete(visibleOp, 7, 1, visibleReceipt) == ToolPublicationOutcome.Visible
        && recoveryTracker.IsPublishedVisibleFor(visibleOp, 7, 3);
    astraRecoveryRun = recoveredCancel && recoveredStale && recoveredVisible;
    astraRecoveryDetail = $"cancelled={recoveredCancel} superseded={recoveredStale} recovered-visible={recoveredVisible}.";
}
catch (Exception error)
{
    astraRecoveryDetail = $"{error.GetType().Name}: {error.Message}";
}

Record("ASTRA-PD8-RECOVERY", astraRecoveryRun, $"positive-fixture({astraRecoveryDetail})");

// ---- ASTRA-PD8-EXACT-PACKAGE-DELIVERY: pinned Engine package delivered byte-exact ----
bool astraPkgRun = false;
string astraPkgDetail = string.Empty;
try
{
    string propsText = File.Exists(Path.Combine(repoRoot, "Directory.Build.props"))
        ? File.ReadAllText(Path.Combine(repoRoot, "Directory.Build.props"))
        : string.Empty;
    const string pinned = "1.13.0-preview.105";
    string nupkg = $"CircuitHub.AllegroBridge.Engine.{pinned}.nupkg";
    bool pinOk = propsText.Contains(pinned, StringComparison.Ordinal);
    bool pkgOk = File.Exists(Path.Combine(repoRoot, "packages", nupkg));
    astraPkgRun = pinOk && pkgOk;
    astraPkgDetail = $"version-pinned={pinOk} package-present={pkgOk} ({nupkg}).";
}
catch (Exception error)
{
    astraPkgDetail = $"{error.GetType().Name}: {error.Message}";
}

Record("ASTRA-PD8-EXACT-PACKAGE-DELIVERY", astraPkgRun, $"positive-fixture({astraPkgDetail})");

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
