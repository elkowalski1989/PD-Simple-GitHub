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
