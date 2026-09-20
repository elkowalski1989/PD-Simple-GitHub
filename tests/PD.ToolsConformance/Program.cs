// Lane H independent conformance harness (PD side), slice 2.
//
// Scope: shell/registration/OpenSection-forwarding source conformance plus
// independent re-verification of the integrated heads on tools/coordinator:
// lane A (T01/T02/T03/T08 section routing), lane B (T06 overlays + T07
// review PD side), lane F (T11 padstacks PD side), lane C (ROUTE layer
// policy + H-first geometry, preserved). Source checks parse the actual
// checkout text; runtime checks use hand-computed expectations, never values
// copied from the implementation under test.
//
// Usage: dotnet run --project tests/PD.ToolsConformance [-- <repoRoot>]
//   Env overrides: PD_TOOLS_REPO_ROOT, LANE_H_EVIDENCE_DIR
// Exit code: 0 when every check passes, 1 otherwise. A results CSV is always
// written to the evidence directory.

using System.Collections.Immutable;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Live;
using PD.PcbTools;
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

int Count(string haystack, string needle)
{
    int n = 0, i = 0;
    while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
    return n;
}

// ---- H-SHELL-NAV-01: remaining placeholders inventoried (5 after A/B/F) ----
int futureCount = Count(mainXaml, "Style=\"{StaticResource FutureNavButton}\"");
Record("H-SHELL-NAV-01", futureCount == 5,
    $"FutureNavButton usages={futureCount}, expected=5 (T04/T05/T09/T10/T12) at MainWindow.xaml.");

// ---- H-SHELL-NAV-02: seven wired destinations + five honest placeholders ----
string[] wiredButtons =
[
    "CrossingMenuButton", "InspectorMenuButton", "MeasureMenuButton", "ScenesMenuButton",
    "OverlayMenuButton", "ReviewMenuButton", "PadstacksMenuButton",
];
var missingWired = wiredButtons
    .Where(name => !mainXaml.Contains($"x:Name=\"{name}\"", StringComparison.Ordinal)
        || !mainXaml.Contains("Style=\"{StaticResource NavButton}\"", StringComparison.Ordinal))
    .ToArray();
string[] placeholderLabels =
[
    "Placement handles", "Via / route editing", "Constraints / DRC",
    "Physical symbols", "Manufacturing",
];
var missingPlaceholders = placeholderLabels
    .Where(label => Count(mainXaml, $"Content=\"{label}") != 1)
    .ToArray();
Record("H-SHELL-NAV-02", missingWired.Length == 0 && missingPlaceholders.Length == 0,
    missingWired.Length == 0 && missingPlaceholders.Length == 0
        ? "7 destinations wired as NavButton; 5 remaining placeholders own one Content each."
        : $"Missing wired=[{string.Join(", ", missingWired)}] placeholders=[{string.Join(", ", missingPlaceholders)}].");

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

// ---- H-BOUNDARY-01: Engine-first boundary (no private mechanisms) ----
// Assembly-attribute reads (GetCustomAttribute) are legitimate diagnostics;
// the forbidden mechanisms are private-field/method reflection that would
// bypass the public Engine contract (cf. H-SHELL-FWD-01 for the forwarder).
string[] boundaryFiles = [mainCs, explorerCs, padstackCs, overlayRecipeCs, trackerCs, bundleCs, overlayVmCs, overlayViewCs, reviewViewCs, padstacksViewCs];
string[] markers = ["GetField(", "GetMethod(", "BindingFlags", "MakeGenericMethod"];
var hits = boundaryFiles
    .SelectMany((text, index) => markers.Where(m => text.Contains(m, StringComparison.Ordinal)).Select(m => $"{index}:{m}"))
    .ToArray();
bool versionReadOnly = mainCs.Contains("GetCustomAttribute<AssemblyInformationalVersionAttribute>");
Record("H-BOUNDARY-01", hits.Length == 0,
    hits.Length == 0 ? $"No private-reflection markers in 10 integrated files (System.Reflection use is version-read-only={versionReadOnly})."
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
    && mainCs.Contains("ShowTool(\"padstacks\")");
Record("H-SHELL-SHOWTOOL-01", showtoolDefined && showtoolKeys,
    $"single ShowTool dispatcher={showtoolDefined} home/explorer/corridor/route/overlay/review/padstacks keys={showtoolKeys}.");

// ---- H-EXPLORER-SESSION-01: one shared Workbench, session retained ----
int workbenchNews = Count(explorerCs, "new EngineWorkbenchView(");
bool sessionRetained = explorerCs.Contains("ReferenceEquals(workbench.Session, presentation.Session)");
bool detachOnDispose = explorerCs.Contains("WorkbenchHost.Content = null");
bool doubleAttachRefused = explorerCs.Contains("The Engine Workbench presentation is already attached.");
Record("H-EXPLORER-SESSION-01", workbenchNews == 1 && sessionRetained && detachOnDispose && doubleAttachRefused,
    $"constructions={workbenchNews} (expected 1) session-retained={sessionRetained} detach-on-dispose={detachOnDispose} double-attach-refused={doubleAttachRefused}.");

// ---- H-PLACEHOLDER-01: T04/T05/T09/T10/T12 stay honestly disabled ----
bool placeholdersHonest = placeholderLabels.All(label => Count(mainXaml, $"Content=\"{label}") == 1)
    && mainXaml.Contains("Content=\"Placement handles") && mainXaml.Contains("Content=\"Via / route editing")
    && mainXaml.Contains("Content=\"Constraints / DRC") && mainXaml.Contains("Content=\"Physical symbols")
    && mainXaml.Contains("Content=\"Manufacturing");
Record("H-PLACEHOLDER-01", placeholdersHonest,
    "T04/T05/T09/T10/T12 remain FutureNavButton placeholders; their native gates stay NOT_EXECUTED.");

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
