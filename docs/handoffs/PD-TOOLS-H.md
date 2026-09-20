# PD Tools lane H handoff (PD repo) — independent conformance + qualification

Date: 2026-09-20. Lane: H (all tools, conformance only). Branch: `tools/h`.
Worker identity for git: lane-h (per-command `-c` flags; no global config change).

## Base / head

- Base (frozen pair): PD `fe26182ebfea8ad0b81ce5a0bce49e280b1198c2`.
- tools/h now tracks tools/coordinator head `5e3fa78` (coordinator prep
  `356bb10` + OpenSection forwarding `7b4341a` + lane C `0871903` + lane F
  `6b96aff` + lane B `42a87ab` + lane A `5e3fa78`), merged here as `495db4f`
  (merge commit; H harness retained, no production source touched by H).
- Slice 1 (prior push `051e5cd`): durable harness + shell/ROUTE slice, 14/14
  at `0871903`. Slice 2 (this commit): harness extended for integrated
  lanes A/B/F + re-qualification at the merged head. No production source
  touched; no MainWindow/registry/pins touched.

## What changed (H-owned files only)

1. `tests/PD.ToolsConformance/Program.cs` (extended, still H-owned):
   - Updated shell inventory to the integrated reality: 5 remaining
     `FutureNavButton` placeholders (T04/T05/T09/T10/T12), 7 wired
     destinations (Crossing/Inspector/Measure/Scenes via lane A,
     Overlay/Review via lane B, Padstacks via lane F).
   - Added `H-NAV-A-01/02` (section routing Crossings/Inspect/Measure/
     Coverage via central `ShowWorkbenchSection`, failure-in-status,
     busy gating, accessible names).
   - Added `H-NAV-B-01..04` (overlay/review ShowTool routing + SyncControls;
     ellipse/polyline shapes + Drag policy + Radii/Points validation with
     independent runtime checks; operation-bound publication receipts with
     stale/unknown/retire isolation; review bundles with hash linkage,
     tamper distinction, schema rejection, atomic-write/no-credentials
     contract).
   - Added `H-NAV-F-01/02` (Padstacks sidebar + ShowTool/refresh gating +
     session-free view; registration `tools.padstacks`/Physical/OpensOffline,
     11 disconnected-gated actions, null-empty, exact unsupported codes).
   - Added `H-BOUNDARY-01` (no private-reflection markers in 10 integrated
     files; `System.Reflection` use is version-attribute-read-only) and
     `H-PLACEHOLDER-01` (T04/T05/T09/T10/T12 honestly disabled).
   - Corrected over-strict boundary probe with evidence: plain
     `GetCustomAttribute<AssemblyInformationalVersion>` in MainWindow.xaml.cs
     is diagnostics, not a contract bypass (forbidden markers are
     GetField(/GetMethod(/BindingFlags/MakeGenericMethod).
   - Evidence writes to `evidence/lane-h/conformance-<stamp>.csv` plus stable
     `evidence/lane-h/conformance-latest.csv` (overridable via
     `LANE_H_EVIDENCE_DIR`).
2. `docs/handoffs/PD-TOOLS-H.md` (this file).
3. `evidence/lane-h/` run CSVs.

## Public contracts used (verified against .93 packages, no invented names)

- `EngineWorkbenchView.OpenSection` + `WorkbenchSection.{Crossings, Inspect,
  Measure, Coverage, Placement, NativeEdits, RoutePreview, Review}` (Wpf).
- `EngineHorizontalFirstRoutePolicy.{SelectLayer, Plan}`,
  `EngineRoutingLayer(Catalog)`, `EngineTraceEndpoints`, `LayerId`,
  `DesignPoint` (Engine).
- `OverlayToolRecipe.{Validate, Build}` + `OverlayToolShape.{Ellipse,
  Polyline}` + `OverlayToolAnchor.Board` + `DrawingBuilder.{Ellipse,
  Polyline}` + `DrawingHitTestPolicy.Drag` (lane B paths).
- `ToolPublicationTracker.{StartOperation, Complete, IsPublishedVisibleFor,
  Retire}` + `ToolPublicationOutcome` (operation-bound receipts).
- `ReviewBundleManifest.{Build, Serialize, TryParse, Sha256Hex,
  WriteFileAtomically, VerifyImages}` (hash-linked, atomic, no authority).
- `PadstackTool.{Registration, DescribeActions, SummarizeDefinitions,
  UnsupportedOperations}` + `PadstackToolActions.*` (11 actions) +
  `EnginePadstackWorkflowOperation` diagnostic codes
  (`padstack_layer_update_unsupported`,
  `padstack_targeted_delete_unsupported`).

## Controls covered (merged head 495db4f = coordinator 5e3fa78 + H)

- NAV-HOME/EXPLORER/DPVC/ROUTE, HDR-ATTACH/SCREENSHOT, CARD-EXPLORER/DPVC/ROUTE,
  ROUTE-01..04, single `ShowTool` dispatcher (null/home + explorer/corridor/
  route/overlay/review/padstacks), OpenSection forwarding contract,
  single-shared-session construction.
- Lane A: Crossing/Inspector/Measure/Scenes named NavButtons with section
  routing + accessibility + busy gating.
- Lane B: Overlay/Review entries + views + gate sync; ellipse/polyline
  recipes + Drag policy; publication tracker outcomes; review bundle
  manifest/hash/atomicity/no-authority contract.
- Lane F: Padstacks entry + refresh gating (busy/offline ShowScene(null));
  session-free view; 11-action availability truth + unsupported codes.
- Lane C (preserved): ROUTE layer-selection policy + H-first plan geometry.
- Remaining placeholders: T04/T05/T09/T10/T12 (5 FutureNavButtons, style
  still gates `IsEnabled=False`).

## Tests (Linux dotnet 10.0.301+, merged head 495db4f)

- `tests/PD.ToolsConformance`: PASS 24/24 (this harness).
- `tests/PD.PcbTools.Checks` (independent re-run of integrated lane C):
  PASS 127.
- `tests/PD.Simple.Checks`: PASS 37 pre-existing + 28 Lane A + session +
  route-completion lines (all PASS).
- `tests/PD.ToolsB.Checks`: PASS (overlay recipes, publication receipts,
  review bundles).
- `tests/PD.PadstackToolChecks`: PASS 14.
- `tests/PD.EngineBoundaryChecks -- .`: PASS (zero lower-SDK deps; WPF absent
  from reusable policy; supports GLOBAL-05 on this head).
- `dotnet build src/PD.Simple/PD.Simple.csproj`: 0 warnings/errors.
- Full log: `evidence/lane-h/conformance-latest.csv` (+ stamped run CSV).

## Gate snapshot (kit ACCEPTANCE_MATRIX.csv; integrated head only)

- PASS (source/runtime, offline): H shell set (NAV-01/02/03, WIRED-01,
  HDR-01, CARD-01, ROUTE-01, FWD-01, SHOWTOOL-01, EXPLORER-SESSION-01,
  PLACEHOLDER-01, BOUNDARY-01 source leg), H-NAV-A-01/02, H-NAV-B-01/02/03/04,
  H-NAV-F-01/02, H-ROUTE-SEL-01/02/03, H-ROUTE-PLAN-01; lane suites green as
  above (T01-01/T01-02/T02-02/T02-03 offline legs via lane A; T03-01/T03-05
  kernel-math legs; T06-01 partial + T06-03/T06-04/T07-01..06 offline legs
  via lane B bounds; T08-01/T08-04/T08-05 request-scope legs; T11 offline
  legs via lane F; ROUTE-01/02 offline legs).
- GLOBAL-05 Engine-first boundary: PASS via boundary gate + H-BOUNDARY-01
  source leg on this head.
- NOT_EXECUTED (all native gates: license gate — only 4 of 7 lanes
  integrated; origin tools/d is baseline, no tools/e branch, origin tools/g
  is baseline): T04-04/05/06, T05-01..06 native legs, ROUTE native legs,
  T06/T07 live publication/capture/DPI legs, T11-01..06 native dispatch legs,
  GLOBAL-07/11/12/18, T09/T10/T12 full matrices.
- NOT_EXECUTED (not yet integrated): T04/T05 views beyond section forwarding
  (no tools/c PD views — lane C proposed fragments only), T09 (no tools/d
  content), T10 (no tools/e branch), T12 (no tools/g content).
- NOT_EXECUTED (environment): runtime UI-Automation tree walk (needs Windows
  + WPF runtime; static XAML walk PASS as substitute here), package-only
  external consumer on exact final packages (needs coordinator release
  generation), performance p95/p99 budgets (needs Windows fixtures +
  repeated sessions), `PD.Simple.DrawingChecks` Windows run.

## Branch-state observations for the coordinator (not qualified; flags only)

- `origin/tools/a` (`c076b25`) == integrated `5e3fa78` parent chain; lane A
  slice touches only lane A buttons + additive forwarding + tests. Clean.
- `origin/tools/b` (`9bf7459`) slice 2 edited coordinator-owned files
  (`MainWindow.xaml(.cs)`, `EngineExplorerView.xaml.cs`); coordinator has
  reconciled them at `42a87ab`. Lane H qualifies the integrated result only.
- `origin/tools/f` (`dee0196`) + bridge `98432f9` gate-scratch hygiene kept
  inside worktree; Engine-first re-check PASS at integration.
- `origin/tools/d` is baseline `fe26182`; no `origin/tools/e`; `origin/tools/g`
  is baseline. Native campaign stays gated until all seven land.
- Lane H runs no builds inside other lanes' worktrees (would dirty them).

## Limits / integration needs

- Native Allegro campaign blocked on the license + all-seven-integrations
  gate above; shared fixture definitions follow kit plan section 20.3
  (disposable copies only). Lane H runs no vendor executables directly —
  native behavior only via dotnet harnesses Lane H writes.
- UI-traversal runtime walk, package-only consumer on final pins, and
  latency/resource budgets are queued for Windows + coordinator release.
- Re-check cadence: origin tools/coordinator (+ both repos) every ~30 min;
  heartbeat commit+push to origin tools/h every 30 min; no force-push; never
  touch other branches.
