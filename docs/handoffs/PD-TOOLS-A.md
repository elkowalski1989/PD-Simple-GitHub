# Lane A handoff — PD Tools (T01, T02, T03, T08 navigation + offline qualification)

Date: 2026-09-20. Worktree: `/mnt/c/e2studio/worktrees/pd-a`, branch `tools/a`.
Base: `fe26182` (== audited PD head; no prior Lane A commits existed on this
branch). Final head: see push receipt at the end of this session.

Scope note: the lane prompt (LANE_A.md) assigns T01/T02/T08; the delegating
objective additionally names T03 (Pick/measure/ruler). T03 routes through the
same thin forwarding to the existing Measure section, so it is included here.
Lane B's deeper interactive pick/native work is untouched.

## Changes (PD-only, Engine packages unchanged at 1.13.0-preview.93)

- `src/PD.Simple/Engine/EngineExplorerView.xaml.cs`: thin public forwarding
  `OpenSection(WorkbenchSection, ObjectFamily? = null)` over the shared
  Workbench. Throws `InvalidOperationException` when the presentation is not
  attached. No new session/presentation, no reflection, no click simulation.
- `src/PD.Simple/MainWindow.xaml`: Crossing review, Geometry inspector,
  Pick/measure/ruler, Captured scenes are now real `NavButton` entries with
  Click handlers and `AutomationProperties.Name`. The other eight entries
  stay `FutureNavButton` placeholders for their lanes.
- `src/PD.Simple/MainWindow.xaml.cs`: central `ShowWorkbenchSection` —
  selects the shared Explorer view, then forwards to Crossings / Inspect /
  Measure / Coverage. Pages open while disconnected (the Workbench shows its
  own setup/offline state and gates live actions); forwarding failures are
  reported in the status line, never as empty success. The four buttons use
  the same busy gating as Board Explorer (route/corridor busy).
- `tests/PD.Simple.Checks/LaneAToolsChecks.cs` (new) + `Program.cs` wiring:
  28 checks — XAML wiring/accessibility, forwarding source contract
  (signature, delegation, missing-Workbench error, forbidden-mechanism scan),
  qualified offline geometry via public Engine APIs (mil/mm equivalence,
  10-mil gap with error bound, diagonal crossing at (5,5) ExactLinear with
  witnesses, disjoint parallels stay Disjoint, 5000-mil/127-mm ruler length,
  zero-length edge), explicit request scope (CrossingQuery rule/layer/
  corridor/representation; SceneQuery region/families/budget, contours
  opt-in only).

## Public contracts used (verified against .93 packages, no invented names)

`EngineWorkbenchView.OpenSection` + `WorkbenchSection.{Crossings, Inspect,
Measure, Coverage}` (Wpf package); `ObjectFamily`, `CrossingQuery`,
`CrossingRepresentation`, `GeometryKernel`, `GeometryPolicy`,
`SceneQuery`, `DataFamily`, `DesignPoint/Length/LayerId/DesignBounds`
(Engine.Core via the Engine package). Verified with disposable in-worktree
probe harnesses (removed after use).

## Tests / commands / results (all in-worktree, Linux)

- `dotnet build src/PD.Simple/PD.Simple.csproj` — succeeded, 0 warnings.
- `dotnet run --project tests/PD.Simple.Checks/PD.Simple.Checks.csproj` —
  37 pre-existing + **28 Lane A** + session-lifetime + route-completion PASS.
- `dotnet run --project tests/PD.EngineBoundaryChecks/... -- .` — PASS (no
  lower-SDK references; WPF absent from reusable policy).
- `dotnet run --project tests/PD.PcbTools.Checks/...` — 123 PASS.

## Gates + results

- T01-01 (known geometry): PASS offline (kernel crossing/disjoint).
- T01-02/T02-03 (semantics/qualified calc): PASS offline (counts vs
  locations distinguished in API; ExactLinear qualification asserted).
- T02-02 (transforms/units): PASS offline (mil/mm equivalence).
- T03-01/T03-05 (ruler math/lifecycle): kernel math PASS offline; live
  picking lifecycle NOT_EXECUTED (needs Allegro).
- T08-01/T08-04/T08-05 (explicit scope/offline/bounds): request-scope PASS
  offline; live capture + archive migration NOT_EXECUTED (needs Allegro).
- GLOBAL-05 (Engine-first boundary): PASS via boundary gate.

## Limits / unresolved (need Windows + licensed Allegro 25.1)

- Live positive/negative fixtures for all four sections (fresh capture,
  crossing Run on a disposable board, native pick lifecycle, archive
  save/open/reopen same-path freshness, busy/reselection/connection-loss,
  DPI/viewport behavior) — NOT_EXECUTED here.
- `tests/PD.Simple.DrawingChecks` cannot execute on Linux (needs
  Microsoft.WindowsDesktop runtime); untouched by this slice, still must run
  on Windows before release.
- Full crossing workflow beyond section routing (subject-route selection,
  finding export/rerun surfacing) and captured-scene manifest export UI
  remain coordinator/integration work; the Workbench sections own the
  actions and their gates.

## Integration needs

- Coordinator owns MainWindow/registry integration; this slice touches only
  Lane A buttons + one additive forwarding method + Lane A tests.
- No shared-contract changes proposed. No new packages.

---

# Slice B — Lane A workflows T01/T02/T08 (views, workspaces, offline qualification)

Date: 2026-09-21. Worktree: `/mnt/c/e2studio/worktrees/pd-a`, branch `tools/a`.
Base: `c076b25` (Slice A navigation, already on origin/tools/a). Final head:
see push receipt at the end of this session.

Slice A (above) routed the four sidebar entries through the shared
Workbench sections. Slice B adds the complete workflows behind them as
additive-only files: three task views plus UI-free workspaces, all routed
through public Engine APIs. No MainWindow, registry, pin, or shared file
was touched in this slice.

## Changes (all new files; Engine packages unchanged at 1.13.0-preview.93)

PD views and workspaces:

- `src/PD.Simple/Tools/Analysis/ToolOperationSupport.cs` — lane-local
  `ToolReadiness`, operation-bound `LaneAOperationResult` (own correlation
  ID, capture identity, timings, diagnostics, artifact hash), atomic
  hash-recorded file output (`LaneAArtifacts`: temp+move, overwrite
  refusal, staging cleanup), and bounded explicit paging (`LaneAPaging`,
  max page 1000, totals always reported).
- `src/PD.Simple/Tools/Analysis/CrossingReviewWorkspace.cs` — T01 over
  static `CrossingAnalyzer.Analyze`: explicit layer/region/representation
  inputs, area-only corridor (Engine rejects lines: corridor is a
  `RegionGeometry` rectangle shell), subject-net narrowing as post-analysis
  rows (Engine semantics untouched), coverage gate with explicit
  acquisition proposal (contours opt in only for copper-area), latest-run
  fencing, cancellation without partial success, finding filter/group/
  select, browse-scope derivation for the Workbench, atomic report export
  with declared counting semantics, rerun/same-capture honesty.
- `src/PD.Simple/Tools/Analysis/GeometryInspectorView.xaml(.cs)` — T02
  task view (family/search/select, local inspect, detail gate, measure
  pair, vertex paging, recipe copy, fact export, automation names).
- `src/PD.Simple/Tools/Analysis/GeometryInspectorWorkspace.cs` — T02 over
  `SceneObjectBrowser` (local-only search/inspect), typed copper detail
  (kind/net/layer/bounds/width/centerline kind/surfaces/role records with
  fidelity/validity/error/source, pin summary), explicit contour loading
  (bounds-only captures refuse with an acquisition proposal), qualified
  pairwise measurement (indeterminate pairs refused, never bounds),
  single-shape vertex paging, `SceneRecipes` recipes, fact export.
- `src/PD.Simple/Tools/Captures/CapturedScenesView.xaml(.cs)` — T08 task
  view (options, capture/cancel/replay/reacquire/close, archive/bulk/
  manifest controls, progress diagnostics, automation names).
- `src/PD.Simple/Tools/Captures/CapturedSceneWorkspace.cs` — T08:
  explicit scope builder (families/contours-opt-in/layers/region/budgets),
  live capture and reacquire through host delegates (each call is a fresh
  native request), bulk open/replay/export/dispose, archive save/open
  (offline-only attach, corrupt/truncated/unknown rejected without a
  scene), offline queries, diagnostic manifest export, close/release that
  preserves exported archives. Archive scenes carry no live authority.
- `src/PD.Simple/Tools/Analysis/LaneARegistration.cs` — descriptor
  fragment (T01/Crossings, T02/Inspect, T08/Coverage with view type names
  and offline notes) for coordinator adoption; declares only, registers
  nothing.

Tests (`tests/PD.ToolsAChecks/`, new net10.0 project, TreatWarningsAsErrors,
links the five UI-free sources — the Wpf-backed views are covered by the
source-level view-contract checks instead):

- `LaneACheck.cs` — deterministic synthetic fixtures from public Engine
  constructors only (`CaptureAcquisitionKind.Synthetic`): 4-trace crossing
  scene (diagonals + duplicate + BOT parallel, all copper kinds in scope,
  layer objects) and a bounds-only truncated partial variant. Fixture
  rules learned from Engine validation are documented inline (absent
  via/pin evidence for traces, width-expanded bounds, all-kinds copper
  scope, null observed counts for unavailable coverage).
- `CrossingWorkspaceChecks.cs` (53) — kernel tangency/overlap/zero-length/
  arc-length/hole/island exact geometry; workspace run (Engine-observed
  3 nets/3 objects/3 locations on TOP, complete clear on BOT),
  truncation/filter/group/select/browse, cancellation, partial-scope and
  degenerate-region gates, report export with hash/overwrite rules.
- `InspectorWorkspaceChecks.cs` (52) — units/negative coords/arc radius,
  bounded search with truncation flags, typed copper detail, per-layer via
  surfaces from the real example board, qualified pair measurement,
  indeterminate-arc honesty, contour gate, vertex paging bounds, recipe,
  fact export, example-board adaptivity.
- `CapturedSceneChecks.cs` (46) — explicit scope/clamping, archive
  save/open same-path identity without live authority, stream round-trip,
  schema ordering, garbage/truncated rejection, bulk-open/replay/export
  negatives, offline source queries, stub-delegate capture/reacquire with
  new identities, manifest export, close/release preservation.
- `ArtifactChecks.cs` (13) — paging totals and rejections, atomic
  write/overwrite/hash/staging rules, result durations.
- `ViewContractChecks.cs` (111) — every `{Binding}` path in the three
  views resolves to a real workspace/row property; every Button has
  `AutomationProperties.Name`; no placeholder styles or forbidden
  mechanisms in code-behinds; registration descriptors exact.
- `NativeGateFixtures.cs` (3 definitions + 3 NOT_EXECUTED) —
  `T01-NATIVE-01`, `T02-NATIVE-01`, `T08-NATIVE-01` with disposable boards,
  explicit queries/budgets, and required live operations. No licensed
  Allegro exists in this lane; all three report NOT_EXECUTED with cause.
  Synthetic fixtures never close these gates.

## Public contracts used (verified against .93 assemblies, no invented names)

`CrossingAnalyzer.Analyze` (static), `CrossingQuery`,
`CrossingRepresentation`, `CrossingAnalysis/Findings` counts and
`CompleteForRequestedRepresentation/IsClear/FindingsTruncated`;
`SceneObjectBrowser` Search/Inspect/Find/CoverageFor, `ObjectFamily`,
`ObjectSearchResult` (Items/TotalMatches/ResultsLimited/Coverage);
`SceneRecipes.Inspect` (static); `GeometryKernel`
Distance/Intersect/Contains/MeasureLength/Sample;
`RelationQualification/GeometricRelation/ContainmentRelation`;
`SceneArchive` Save/Load/Write/Read (static) + schema ordering;
`EngineBulkCapture` OpenAsync (static)/Replay/Export/Dispose;
`OfflineDesignSource.AcquireAsync`; `SceneRebinding` noted (rebind on
reacquire is host-executed); `AllegroWorkspace.ReadAsync` and
`CaptureBulkSceneAsync` are host-delegate injected, never constructed
here. `EngineExamples.CreateBoard` (static) is an offline test input
only, labeled synthetic.

## Tests / commands / results (Windows dotnet 10.0.301 via WindowsPowerShell)

- `dotnet build src/PD.Simple/PD.Simple.csproj -c Release` — 0 warnings,
  0 errors (Windows and Linux).
- `dotnet run --project tests/PD.ToolsAChecks` — 278 PASS (53 T01 + 52
  T02 + 46 T08 + 13 artifacts + 111 view contracts + 3 fixture
  definitions); 3 native gates NOT_EXECUTED (no licensed Allegro; slot
  scheduled by coordinator). Same result on Linux and Windows.
- `dotnet run --project tests/PD.Simple.Checks` — pre-existing 37 +
  Slice-A 28 + session/route PASS, unchanged (Windows and Linux).
- `dotnet run --project tests/PD.PcbTools.Checks` — 123 PASS, unchanged.
- `dotnet run --project tests/PD.EngineBoundaryChecks -- .` — PASS (no
  lower-SDK references; new code uses Engine publicly, Wpf only in views).
- `Build.ps1` (ExecutionPolicy Bypass) — publish green, installer
  verified, `artifacts/PD-Simple-Setup.zip` produced.

Evidence: `/mnt/c/e2studio/_lanes/a/` — `tools-a-checks-windows.log`
(278 PASS), `release-publish-windows.log` (publish green),
`base-sha.txt`, `worktree-status.txt`.

## Gates + results (Slice B delta)

- T01-01 (known geometry): PASS offline (tangency/overlap/hole/island/arc
  kernel geometry; analyzer 3/3/3 on fixture, clear-on-BOT).
- T01-02/T01-03/T01-04/T01-05/T01-06: PASS offline (declared counting
  semantics; copper-kind completeness gate; filter/group/select/browse/
  export/rerun; archive replay equivalence via round-trip identity;
  cancellable runs, no whole-board recollection for details).
- T02-01..T02-06: PASS offline (typed fields/roles/fidelity/validity/
  error/provenance; mil/mm/inch + negatives; indeterminate refusal;
  explicit contour loading; recipe + fact export; single-shape paging).
- T08-01..T08-06: PASS offline except live capture/replay (explicit
  scope/budgets; schema ordering; garbage/truncated rejection; offline
  queries without authority; close/release; stubbed reacquire with new
  identities; same-path archive identity without authority).
- GLOBAL-05 (Engine-first boundary): PASS via boundary gate.
- All T01/T02/T08 native-positive/negative fixtures: NOT_EXECUTED with
  disposable-board definitions ready (see NativeGateFixtures.cs).

## Limits / unresolved (need Windows + licensed Allegro 25.1)

- Live positive/negative fixtures T01-NATIVE-01/T02-NATIVE-01/
  T08-NATIVE-01 — NOT_EXECUTED here (single-license slot scheduled later
  by the coordinator; lane performed no Allegro/native runs).
- `tests/PD.Simple.DrawingChecks` still needs the Windows runtime;
  untouched by this slice.
- The three views are compile-verified plus source-level binding-named
  verified (111 checks); runtime UI Automation tree-walk over the views
  belongs to integration with a live session.
- DP corridor interpretation unchanged (T01 carries its own rule
  identity; corridor findings are never reused as crossing evidence).

## Integration needs (coordinator)

- Adopt `LaneAToolDescriptor.All` into the central registry and host the
  three views (`CrossingReviewView`, `GeometryInspectorView`,
  `CapturedScenesView`) with live-wired workspaces:
  `CrossingReviewWorkspace(acquireLiveScene:
  (query, token) => Workspace.ReadAsync(query, token).Scene)`,
  `CapturedSceneWorkspace(acquireScene: ..., acquireBulk:
  (options, progress, token) => Workspace.CaptureBulkSceneAsync(options, token))`.
  Suggested navigation: the existing `ShowWorkbenchSection` entries stay
  as the fast path; the new views are the full task pages. Exact patch:

  ```csharp
  // Coordinator-owned MainWindow/registry code (proposal, not applied):
  // _crossingView = new CrossingReviewView
  // {
  //     Review = new CrossingReviewWorkspace(async (query, token) =>
  //         (await _bridge.Workspace.ReadAsync(query, token)).Scene),
  // };
  ```

  (`ReadAsync` returns `ValueTask<LiveDesignScene>`; the workspace only
  needs `Func<SceneQuery, CancellationToken, Task<DesignScene>>`.)
- No shared-contract changes in this slice. If the coordinator promotes
  `ToolReadiness`/`LaneAOperationResult`/`LaneAArtifacts`/`LaneAPaging`
  to shared contracts, Lane A will rebase onto them.
- No new packages. No MainWindow/registry/pin edits by this lane.
