# Lane B handoff: T06 Live overlay tools, T07 Share/review view

Scope: PD Tools/Interaction + Tools/Review (T06, T07). T03 (pick/measure)
is excluded from this lane's slices; it is owned elsewhere.

## Source

- Worktree: `/mnt/c/e2studio/worktrees/pd-b`, branch `tools/b` (PD-only).
- Base: `fe26182` (kit-frozen PD head; slices build on it, no reset).
- Slices on this branch:
  - `3f87dbf` slice 1: overlay recipes, publication tracker, review bundles,
    Linux checks (`tests/PD.ToolsB.Checks`).
  - `ffb6753` slice 2: T06/T07 views + view-models, thin `OpenSection`
    forwarding on `EngineExplorerView`, sidebar wiring in MainWindow.
  - slice 3 (this commit): ellipse/polyline shapes, Drag hit-test,
    anchor navigation opens Explorer Inspect, Windows gate checks,
    initial-disabled XAML states.
- Bridge/Engine contract: `1.13.0-preview.93` packages from the worktree
  `.packages` feed (`NuGet.Config`: `packages` source, `.packages` global
  folder). API names below were verified against those exact binaries with
  a disposable reflection probe (see Evidence), not invented.

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

| Command (worktree root) | Result |
|---|---|
| `dotnet run --project tests/PD.ToolsB.Checks/PD.ToolsB.Checks.csproj` | PASS: overlay recipes (line/circle/ellipse/rect/polygon/open+closed polyline/text/marker/dimension, object anchor), validation negatives, tracker, bundles |
| Disposable Roslyn parse gate over 11 touched WPF/test C# files | PASS, 11/11 (syntax only; full WPF compile needs Windows) |
| Disposable reflection probe vs `.packages` 1.13.0-preview.93 | All used API names confirmed (see Changes); full member lists in commit message thread / probe source |

Windows-only (NOT executed on this Linux box, no Cadence license here):
`tests/PD.Simple.DrawingChecks` with `ToolsBChecks.Run()`, full
`PD.Simple` build, live publication/capture/hide/remove against a
disposable board, DPI/pan/zoom/minimize behavior, recording-mode
qualification, performance budgets.

## Per-tool acceptance mapping

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
(manifest-linked filenames only), native-pending.

## Limits and unresolved operations (with refs)

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
  (forwarding only). No registry/contract changes proposed.
- Confirm the two sidebar destinations are registered through the
  central tool registry (slice 2 wired `ShowTool("overlay"/"review")`
  buttons; registry ownership is coordinator's).
- Untracked scratch left in the worktree for your disposal:
  `scratch-tools-b-probe/` (API probe + parse gate harnesses).
  Do NOT commit it; delete after use.
- Native qualification needs an isolated Allegro/Windows slot with a
  disposable board; lane B must not share a live board session with
  another lane's Host/resident.
