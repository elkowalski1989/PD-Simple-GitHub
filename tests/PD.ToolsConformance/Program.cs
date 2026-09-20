// Lane H independent conformance harness (PD side).
//
// Scope: shell/registration/OpenSection-forwarding source conformance plus
// independent re-verification of the integrated ROUTE layer-selection policy
// (lane C, tools/coordinator). Source checks parse the actual checkout text;
// runtime checks use hand-computed expectations, never values copied from the
// implementation under test.
//
// Usage: dotnet run --project tests/PD.ToolsConformance [-- <repoRoot>]
//   Env overrides: PD_TOOLS_REPO_ROOT, LANE_H_EVIDENCE_DIR
// Exit code: 0 when every check passes, 1 otherwise. A results CSV is always
// written to the evidence directory.

using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Live;
using PD.PcbTools;

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

string mainXaml = File.Exists(mainXamlPath) ? File.ReadAllText(mainXamlPath) : string.Empty;
string mainCs = File.Exists(mainCsPath) ? File.ReadAllText(mainCsPath) : string.Empty;
string explorerCs = File.Exists(explorerCsPath) ? File.ReadAllText(explorerCsPath) : string.Empty;

int Count(string haystack, string needle)
{
    int n = 0, i = 0;
    while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
    return n;
}

// ---- H-SHELL-NAV-01: twelve placeholder destinations inventoried ----
string[] toolLabels =
[
    "Crossing review", "Geometry inspector", "Pick / measure / ruler",
    "Placement handles", "Via / route editing", "Live overlay tools",
    "Share / review view", "Captured scenes", "Constraints / DRC",
    "Physical symbols", "Padstacks", "Manufacturing",
];
int futureCount = Count(mainXaml, "Style=\"{StaticResource FutureNavButton}\"");
Record("H-SHELL-NAV-01", futureCount == 12,
    $"FutureNavButton usages={futureCount}, expected=12 at MainWindow.xaml.");

// ---- H-SHELL-NAV-02: each tool owns exactly one sidebar button ----
// Labels may also appear in honest placeholder notices elsewhere in the page;
// the conformance claim is one FutureNavButton Content= per label.
var missing = toolLabels.Where(label => Count(mainXaml, $"Content=\"{label}") != 1).ToArray();
Record("H-SHELL-NAV-02", missing.Length == 0,
    missing.Length == 0 ? "All 12 tool labels own exactly one sidebar button Content."
        : $"Button Content count != 1: {string.Join(", ", missing)}.");

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

// ---- H-ROUTE-SEL-*: independent layer-selection verification ----
WorkspaceDocumentIdentity doc = new("lane-h-proof", 1, 2, null, "lane-h-proof.brd", "data-only");
EngineRoutingLayer Layer(string id, bool visible, bool active) => new(new(id), false, visible, active);

EngineRoutingLayerCatalog Catalog(params EngineRoutingLayer[] layers) => new(doc, layers);

bool selPass = true;
try
{
    // Single active visible wins even when listed second.
    var one = EngineHorizontalFirstRoutePolicy.SelectLayer(Catalog(
        Layer("ETCH/TOP", true, false), Layer("ETCH/S03", true, true)));
    selPass &= one.Id == new LayerId("ETCH/S03");

    // No active layer falls back to the first visible layer.
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

// Active-but-invisible layer carries no authority: ignored, first visible wins.
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

// Ambiguity and emptiness are rejected with the documented exception kinds.
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
    // Horizontal-first: (1,2) -> (11,2) -> (11,20).
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

// ---- H-SHELL-SHOWTOOL-01: preserved destinations share one dispatcher ----
bool showtoolDefined = mainCs.Contains("private void ShowTool(string? tool)");
bool showtoolKeys = mainCs.Contains("ShowTool(null)") && mainCs.Contains("ShowTool(\"explorer\")")
    && mainCs.Contains("ShowTool(\"corridor\")") && mainCs.Contains("ShowTool(\"route\")");
Record("H-SHELL-SHOWTOOL-01", showtoolDefined && showtoolKeys,
    $"single ShowTool dispatcher={showtoolDefined} home/explorer/corridor/route keys={showtoolKeys}.");

// ---- H-EXPLORER-SESSION-01: one shared Workbench, session retained ----
int workbenchNews = Count(explorerCs, "new EngineWorkbenchView(");
bool sessionRetained = explorerCs.Contains("ReferenceEquals(workbench.Session, presentation.Session)");
bool detachOnDispose = explorerCs.Contains("WorkbenchHost.Content = null");
bool doubleAttachRefused = explorerCs.Contains("The Engine Workbench presentation is already attached.");
Record("H-EXPLORER-SESSION-01", workbenchNews == 1 && sessionRetained && detachOnDispose && doubleAttachRefused,
    $"constructions={workbenchNews} (expected 1) session-retained={sessionRetained} detach-on-dispose={detachOnDispose} double-attach-refused={doubleAttachRefused}.");

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
