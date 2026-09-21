# Lane B handoff: T03 Pick/measure/ruler, T06 Live overlay tools, T07 Share/review view

Scope: PD Tools/Interaction + Tools/Review (T03, T06, T07).

## Source

- Worktree: `/mnt/c/e2studio/worktrees/pd-b`, branch `tools/b` (PD-only).
- Base: `fe26182` (kit-frozen PD head; slices build on it, no reset).
- Slices on this branch:
  - `3f87dbf` slice 1: overlay recipes, publication tracker, review bundles,
    Linux checks (`tests/PD.ToolsB.Checks`).
  - `ffb6753` slice 2: T06/T07 views + view-models, thin `OpenSection`
    forwarding on `EngineExplorerView`, sidebar wiring in MainWindow.
  - `9bf7459` slice 3: ellipse/polyline shapes, Drag hit-test,
    anchor navigation opens Explorer Inspect, Windows gate checks,
    initial-disabled XAML states.
  - slice 4 (this commit): T03 pick/measure/ruler (captured + native live
    pick, snap policy, persistent rulers with receipts, units, copy/export,
    advisory-vs-native separation, Escape cancel) plus a T07
    first-execution init fix (review zoom handler guard).
- Bridge/Engine contract: `1.13.0-preview.93` packages from the worktree
  `.packages` feed (`NuGet.Config`: `packages` source, `.packages` global
  folder). API names below were verified against those exact binaries with
  a disposable reflection probe (see Evidence), not invented. T03 reuses the
  current native pick (`AllegroWorkspacePicking.StartTwoPointPickAsync`) per
  plan section 7; no lightweight pick contract was extracted (no profiling
  evidence that routing-context acquisition is materially expensive).

## Changes (slice 4: T03 + T07 init fix)

1. `src/PD.PcbTools/MeasureTools/MeasureModels.cs` (new)
   - `MeasureEndpoint` (raw + snapped board points, snap description,
     `Captured` vs `NativeObservation` provenance, native kind/net),
     `MeasuredSpan` (deltas, mil + mm distance, atan2 angle, degenerate
     flag, capture/document binding), `MeasureMath` (pure computation,
     `DesignSnapping.Grid` reuse, export text + public C# recipe).
   - No native I/O, no copper mutation, no screen-pixel arithmetic.
2. `src/PD.PcbTools/MeasureTools/MeasureSession.cs` (new)
   - Empty/FirstHeld/Complete state machine, clear-first/cancel annulment
     with epoch advance, operation-bound pick ids with superseded-pick
     fencing, persistent rulers with add/move/remove and a revision chain.
3. `src/PD.Simple/Tools/Interaction/MeasureToolViewModel.cs` (new)
   - Captured endpoints with No-snap/Grid-1-mil/Custom-grid policy;
     native two-point pick lifecycle (start, feedback drain with
     currentness fencing, terminal admission, result document check,
     ClearFirstAsync, explicit CancelAsync request); Escape path via view;
     auto-kept persistent rulers published as display-only dimension
     drawings through `OverlayToolRecipe` + `ToolPublicationTracker` with
     per-request receipts; mil/mm switch; clipboard copy + atomic .txt
     export (overwrite refused); advisory captured-object presence check
     labeled as non-authority; disconnected gates on every action; shared
     session/presentation borrowed, never created or disposed.
4. `src/PD.Simple/Tools/Interaction/MeasureToolView.xaml(.cs)` (new)
   - Six-section task panel, initial-disabled buttons synced from gates,
     `AutomationProperties` names, Escape cancels the in-flight pick.
   - NOT wired into MainWindow (coordinator-owned): registration fragment
     in "Integration needs" below.
5. `src/PD.Simple/Tools/Review/ShareReviewToolView.xaml.cs`
   - `Zoom_Changed` ignores notifications while its elements do not exist
     yet: `Value="1"` fires during `InitializeComponent` before the
     later-declared `ZoomText` exists (first Windows execution found this
     as an NRE). No post-load behavior change.
6. `tests/PD.ToolsB.Checks/MeasureChecks.cs` (new, Linux/Windows):
   3-4-5 span, same point, negatives, mil/mm, grid snap + raw retention,
   native provenance, session lifecycle, superseded fencing, ruler
   lifecycle, ruler recipe build, export text, validation negatives.
7. `tests/PD.Simple.DrawingChecks/ToolsBChecks.cs`:
   `CheckMeasureDisconnectedGates` (all action gates false offline,
   gated calls are no-ops, zero Engine operations, no state produced,
   shared session/presentation survive tool disposal).

## Changes (slice 3)

1. `src/PD.PcbTools/OverlayTools/OverlayToolRecipe.cs`
   - New `OverlayToolShape.Ellipse(center, rx, ry)` and
     `OverlayToolShape.Polyline(points, closed)` with validation
     (`Radii`, `Points`), Engine build, and recipe description.
   - Verified target APIs in .93 `Engine.Core`:
     `DrawingBuilder.Ellipse(LocalPoint, Length, Length)` and
     `DrawingBuilder.Polyline(IEnumerable<LocalPoint>, bool closed = false)`.
2. `src/PD.Simple/Tools/Overlay/LiveOverlayToolViewModel.cs`
   - Shapes list adds `Ellipse`, `Polyline`; new `EllipseParams`,
     `PolylineParams`, `PolylineClosed` inputs with parse/validation.
   - Hit-test options add `Drag` (`DrawingHitTestPolicy.Drag` verified
     in .93); mapping `None/Select/Drag`, default `None`.
3. `src/PD.Simple/Tools/Overlay/LiveOverlayToolView.xaml(.cs)`
   - `EllipsePanel` (`EllipseInput`), `PolylinePanel` (`PolylineInput`,
     `PolylineClosedCheck`); panel switching, push/pull sync.
4. Action buttons now declare `IsEnabled="False"` in XAML
   (`LiveOverlayToolView.xaml`, `ShareReviewToolView.xaml`).
   This is the safe initial state only: both code-behinds sync every
   button from the view-model gates (`SyncControls`), so pages open
   disconnected with actions disabled and setup guidance visible.
5. `src/PD.Simple/MainWindow.xaml.cs` (coordinator-owned file; additive
   change to lane B's own handler, flagged for integration review):
   anchor-pick navigation now calls
   `ExplorerView.OpenSection(WorkbenchSection.Inspect)` after
   `ShowTool("explorer")`, guarded with a status message if the shared
   Workbench is unattached. `WorkbenchSection.Inspect` verified in .93
   `CircuitHub.AllegroBridge.Wpf` (`Wpf.Engine` namespace, members:
   Coverage, Crossings, Inspect, Measure, NativeEdits, Placement,
   Review, RoutePreview).
6. `tests/PD.ToolsB.Checks/OverlayRecipeChecks.cs`: ellipse/open-closed
   polyline build cases plus `Radii`/`Points` negative cases.
7. `tests/PD.Simple.DrawingChecks/ToolsBChecks.cs` (new, Windows-only):
   disconnected gate checks for both VMs (all action gates false
   offline, gated calls are no-ops, zero Engine operations recorded,
   shared session/presentation survive tool disposal), recipe
   zero-mutation check, bundle reopen-offline check with hash
   verification and manifest-linked removal. Hooked into
   `tests/PD.Simple.DrawingChecks/Program.cs`.

## Design invariants (kept)

- One shared Engine session + one shared `EngineWpfPresentation`,
  created in `MainWindow`, borrowed by both tools and the Explorer
  Workbench. No tool creates/disposes a session, Host, or Workbench.
- Tool views own only their drawings, operations, and subscriptions.
- Pages open while disconnected; gates sit on actions, with the reason
  shown beside the control. Review capture uses the dedicated
  `CaptureReviewAsync` path and never drives fast Browse.
- Diagnostics off by default: review images exist only from explicit
  Capture; no screenshot logging added (retained-image pruning deletes
  only manifest-linked filenames).
- Outputs are atomic (`WriteFileAtomically`), PNG overwrite is refused,
  bundles carry hashes and verify on reopen; reopened bundles grant no
  live authority and carry no credentials/tokens/handles.
- No private reflection, no click simulation, no platform code copied
  into PD.

## Tests and results

| Command (Windows, Release, powershell dotnet 10.0.301) | Result |
|---|---|
| `dotnet build src/PD.Simple/PD.Simple.csproj -c Release` | Build succeeded. 0 Warning(s). 0 Error(s). |
| `dotnet run --project tests/PD.ToolsB.Checks/PD.ToolsB.Checks.csproj -c Release` | PASS: Lane B shared tool logic (overlay recipes, publication receipts, review bundles, pick/measure). |
| `dotnet run --project tests/PD.Simple.DrawingChecks/PD.Simple.DrawingChecks.csproj -c Release` | PASS (includes ToolsB overlay/review/measure gate checks, zero-mutation, bundle reopen). One pre-existing CS8602 warning in Program.cs(512,14); file untouched. |
| `dotnet run --project tests/PD.PcbTools.Checks/PD.PcbTools.Checks.csproj -c Release` (regression) | PASS: 123 checks. |
| `dotnet run --project tests/PD.EngineBoundaryChecks/PD.EngineBoundaryChecks.csproj -c Release` (regression) | PASS: zero lower-SDK dependencies; WPF absent from reusable policy. |
| `dotnet run --project tests/PD.Simple.Checks/PD.Simple.Checks.csproj -c Release` (regression) | PASS x3. |
| Linux dotnet inner loop (PD.PcbTools, PD.Simple, DrawingChecks builds + ToolsB checks) | All green, 0 warnings/errors. |

Windows-only first-execution finding: DrawingChecks failed on first run in
pre-existing slice-3 `CheckReviewDisconnectedGates` (review zoom-handler NRE
during XAML init); fixed in slice 4, rerun PASS. Stack trace retained at
`/mnt/c/e2studio/_lanes/b/drawingchecks-first-failure.txt`.

Native-only (NOT_EXECUTED, no licensed Allegro on this box, slot scheduled
later by coordinator): live two-point pick lifecycle, live ruler publication
receipts, review capture on a real canvas, DPI/pan/zoom/minimize behavior,
recording-mode qualification, performance budgets, disk-full fault
injection. Fixture definitions ready:
`/mnt/c/e2studio/_lanes/b/native-fixtures-t03.md`.

## Per-tool acceptance mapping

T03: T03-01 implemented (captured deltas/distance/angle + snapped/original
endpoints), native-pending; T03-02 implemented (full native
pick/feedback/terminal/result lifecycle, clear-first, cancel, no drawing
converted to authority), native-pending; T03-03 implemented (clear-first,
Escape, cancel at each stage, lost-connection truthful outcome),
native-pending; T03-04 implemented by construction (no PD screen math,
canonical mils, RequireCurrent + document check on admission, non-current
feedback skipped; viewport evidence stays Engine-owned), environment-pending;
T03-05 implemented (auto-kept/movable/removable rulers, mil/mm switch,
copy/export), native-pending; T03-06 by construction (read/pick/present
APIs only; zero-mutation asserted offline), native readback pending.
T06: T06-01 partial (see U1); T06-02 implemented, native-pending;
T06-03 implemented (operation-bound receipts, pending/visible/empty/
unavailable/superseded/cancelled), native-pending; T06-04 implemented
(None/Select/Drag policies; gestures stay Engine-owned), native-pending;
T06-05 gates implemented, environment-pending; T06-06 unmeasured (U5),
recording modes labeled-not-qualified.
T07: T07-01/T07-03/T07-04 implemented, native-pending; T07-02 by
construction (dedicated capture path, cancel/failure paths leave
navigation drawings untouched); T07-05 implemented for I/O paths
(atomic writes, overwrite refusal, no partial-file advertising),
disk-full fault injection pending; T07-06 implemented
(manifest-linked filenames only), native-pending. Slice 4 adds the
review zoom init fix (first Windows execution finding).

## Limits and unresolved operations (with refs)

- U0 (T03): all live native gates (two-point pick lifecycle, live ruler
  publication/hide/remove receipts, PNG dimensions/DPI on a real canvas)
  need a licensed Allegro session + Windows runner with a disposable board.
  Status stays NOT_EXECUTED with cause, never mock-passed. Fixture
  definitions ready: `/mnt/c/e2studio/_lanes/b/native-fixtures-t03.md`.
  Captured object snap beyond the Engine grid policy has no public
  captured-geometry query path: snap modes are No-snap/Grid-1-mil/
  Custom-grid for captured points; object kind/net authority rides on live
  native picks only. Not simulated.
- U1 (T06-01): curved/polygon regions WITH HOLES (and islands) have no
  public drawing path: .93 `DrawingBuilder` exposes Line, Circle,
  Ellipse, Rectangle, Polygon, Polyline, Text, Marker, Measurement only
  (probe: `DrawingBuilder` member list, no region/hole API).
  Plan ref: TECHNICAL_IMPLEMENTATION_PLAN.md section 10 ("polygon/curved
  region with holes"). Needs an Engine drawing extension or a qualified
  `LocalGeometry(GeometryRecord, …)` holes recipe from vendor docs;
  not simulated.
- U2: all live native gates (publish/hide/remove receipts, review
  capture, PNG dimensions/DPI on real canvas) need a licensed Allegro
  session + Windows runner with a disposable board. Status stays
  NOT_EXECUTED with cause, never mock-passed.
- U3: `PD.Simple` full build + `PD.Simple.DrawingChecks` execution
  pending on Windows (this box is Linux, net10.0-only).
- U4 (T06-06): sharing/recording compatibility is labeled in UI
  ("not a recording-compatibility promise") and unqualified.
- U5: performance budgets (plan section 19) unmeasured here.

## Integration needs for the coordinator

- Merge conflict surface: `MainWindow.xaml(.cs)` (lane B handler +
  guarded `OpenSection` call), `EngineExplorerView.xaml.cs`
  (forwarding only). No registry/contract changes proposed. Slice 4 does
  NOT touch `MainWindow.*`: the T03 page below is delivered unwired.
- Confirm the two sidebar destinations are registered through the
  central tool registry (slice 2 wired `ShowTool("overlay"/"review")`
  buttons; registry ownership is coordinator's).
- T03 registration fragment (proposed patch, coordinator to apply; slice 4
  added no `MainWindow`/`EngineExplorerView` edits):
  - `MainWindow.xaml`: add
    `xmlns:measure="clr-namespace:PD.Simple.Tools.Interaction"`, a
    `Pick / measure / ruler` NavButton (`x:Name="MeasureMenuButton"`,
    `Click="Measure_Click"`) under INTERACT + EDIT, and
    `<measure:MeasureToolView x:Name="MeasureView" Visibility="Collapsed"/>`
    beside the other tool views.
  - `MainWindow.xaml.cs`: construct
    `_measure = new MeasureToolViewModel(_bridge, _presentation);`,
    set `MeasureView.ViewModel = _measure;`, navigate with
    `ShowTool("measure")` from `Measure_Click` plus the same guarded
    `ExplorerView.OpenSection(WorkbenchSection.Measure)` pattern used for
    Inspect on anchor navigation (`MeasureToolViewModel.
    NavigateToExplorerRequested`), extend `ShowTool` visibility switching,
    `UpdateControls` menu gating, and `DisposeApplicationAsync` teardown
    for the new view model. No other shared-file changes are needed.
- Untracked scratch left in the worktree for your disposal:
  `scratch-tools-b-probe/` (API probe + parse gate harnesses).
  Do NOT commit it; delete after use.
- Native qualification needs an isolated Allegro/Windows slot with a
  disposable board; lane B must not share a live board session with
  another lane's Host/resident.
