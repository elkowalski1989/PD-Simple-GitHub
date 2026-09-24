# PD Tools lane H handoff (PD repo) — independent conformance + qualification

Date: 2026-09-20. Lane: H (all tools, conformance only). Branch: `tools/h`.
Worker identity for git: lane-h (per-command `-c` flags; no global config change).

> Reconciliation note (2026-09-22, Muse simplification workstream): the
> body below is the frozen 2026-09-20 record (pins at `.93`, 31/31
> source-text conformance). Current state — `.104` pins, behavior
> conformance, remediated defects — is recorded in the addendum at the
> end of this file. Do not read the version pins or check counts in the
> body as current.

## Base / head

- Base (frozen pair): PD `fe26182ebfea8ad0b81ce5a0bce49e280b1198c2`.
- tools/h now tracks tools/coordinator head `aec6013` (prior `5e3fa78`
  + lane G `c5a1838` + Manufacturing wiring `9b2c39a` + lane D `b9ab940`
  + Constraints/DRC wiring `1f0deec` + lane E `344b876` + Physical-symbols
  wiring `aec6013`), merged here as `9e9ea07` (merge commit; H harness
  retained, no production source touched by H).
- Slice 1 (`051e5cd`, pushed): durable harness + shell/ROUTE slice, 14/14.
- Slice 2 (`be89309`, pushed after preserving the stalled predecessor's
  staged work): harness extended for integrated lanes A/B/F, 24/24.
- Slice 3 (this commit): harness extended for the full 7-lane surface
  (lanes D/E/G + lane C T04/T05 section targets), 31/31 at the merged
  head. No production source touched; no MainWindow/registry/pins touched.

## What changed (H-owned files only)

1. `tests/PD.ToolsConformance/Program.cs` (extended, still H-owned):
   - Updated shell inventory to the integrated reality: 2 remaining
     `FutureNavButton` placeholders (T04/T05), 10 wired destinations
     (Crossing/Inspector/Measure/Scenes via lane A, Overlay/Review via
     lane B, Padstacks via lane F, Constraints/DRC via lane D,
     Physical symbols via lane E, Manufacturing via lane G).
   - Added `H-NAV-C-01` (T04/T05 honest placeholders + lane C
     Placement/NativeEdits/RoutePreview section fragment + offline-page
     note; native T04/T05 gates stay NOT_EXECUTED).
   - Added `H-NAV-D-01/02` (Constraints/DRC sidebar + ShowTool +
     AttachSession/single-attach/dispose wiring; PendingPackageReason
     naming the exact missing Engine APIs + frozen .93 pin; effective
     reads/typed edits/Run DRC disabled; read-vs-execution honesty:
     snapshots never labeled assigned/effective, marker reads never
     claim execution, no manufactured violating-object IDs).
   - Added `H-NAV-E-01/02` (Physical-symbols sidebar + ShowTool +
     busy/offline refresh gating + session-free view; registration
     `tools.physical-symbols`/Physical/OpensOffline; 17 ops / 21
     actions, disconnected-all-gated, reason-equals-Engine-truth,
     no command-ID leak, empty production list, offline four-pad
     inspection, 3 vendor limits with exact codes).
   - Added `H-NAV-G-01/02` (Manufacturing sidebar + ShowTool +
     Attach/RefreshFromSession + Unqualified default runner + IsEnabled
     gating + Mfg automation IDs/reason text; offline-first page model:
     disconnected refusal, no-acquire-while-disconnected, missing
     source, ready fence, valid/invalid Artwork/ODB++/IPC-2581 plans,
     all three honest NoGo codes, promotion refusal).
   - Boundary file list extended 10 -> 17 integrated files; ShowTool
     key list extended to manufacturing/constraintsdrc/physicalsymbols;
     placeholder check narrowed to T04/T05 with a stale-placeholder
     guard for T09/T10/T12.
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

## Controls covered (merged head 9e9ea07 = coordinator aec6013 + H)

- NAV-HOME/EXPLORER/DPVC/ROUTE, HDR-ATTACH/SCREENSHOT, CARD-EXPLORER/DPVC/ROUTE,
  ROUTE-01..04, single `ShowTool` dispatcher (null/home + explorer/corridor/
  route/overlay/review/padstacks/manufacturing/constraintsdrc/
  physicalsymbols), OpenSection forwarding contract, single-shared-session
  construction.
- Lane A: Crossing/Inspector/Measure/Scenes named NavButtons with section
  routing + accessibility + busy gating.
- Lane B: Overlay/Review entries + views + gate sync; ellipse/polyline
  recipes + Drag policy; publication tracker outcomes; review bundle
  manifest/hash/atomicity/no-authority contract.
- Lane F: Padstacks entry + refresh gating (busy/offline ShowScene(null));
  session-free view; 11-action availability truth + unsupported codes.
- Lane C (preserved): ROUTE layer-selection policy + H-first plan geometry;
  T04/T05 section targets (Placement/NativeEdits/RoutePreview) per the
  lane C registration fragment; offline-page note.
- Lane D: Constraints/DRC entry + ShowTool + AttachSession/single-attach/
  dispose; snapshot-vs-execution honesty; typed edits + Run DRC disabled
  with PendingPackageReason.
- Lane E: Physical-symbols entry + ShowTool + busy/offline refresh gating;
  session-free view; 21-action policy truth + 3 vendor limits.
- Lane G: Manufacturing entry + ShowTool + Attach/RefreshFromSession;
  Unqualified default runner; offline-first page model + honest NoGo.
- Remaining placeholders: T04/T05 only (2 FutureNavButtons, style still
  gates `IsEnabled=False`).

## Tests (Linux dotnet 10.0.301+, merged head 9e9ea07)

- `tests/PD.ToolsConformance`: PASS 31/31 (this harness, full 7-lane
  surface: shell + A/B/C/D/E/F/G + ROUTE + boundary + session).
- `tests/PD.PcbTools.Checks`: PASS 144 (routing policy, corridor,
  coverage, classification, navigation-witness, manufacturing page).
- `tests/PD.Simple.Checks`: PASS 37 pre-existing + 28 Lane A +
  28 Lane A navigation/forwarding/offline-geometry + session +
  route-completion + 29 Constraints/DRC offline gates/marker-review/
  comparison/export (all PASS).
- `tests/PD.ToolsB.Checks`: PASS (overlay recipes, publication receipts,
  review bundles).
- `tests/PD.PadstackToolChecks`: PASS 14.
- `tests/PD.PhysicalSymbolToolChecks`: PASS 72 (17-op inventory,
  reason-truth, offline fixture, gating, vendor limits).
- `tests/PD.EngineBoundaryChecks -- .`: PASS (zero lower-SDK deps; WPF absent
  from reusable policy; supports GLOBAL-05 on this head).
- `dotnet build src/PD.Simple/PD.Simple.csproj`: 0 warnings/errors
  (includes ConstraintsDrc + PhysicalSymbols + Manufacturing views).
- Full log: `evidence/lane-h/conformance-latest.csv` (+ stamped run CSV).

## Gate snapshot (kit ACCEPTANCE_MATRIX.csv; integrated head only)

- PASS (source/runtime, offline): H shell set (NAV-01/02/03, WIRED-01,
  HDR-01, CARD-01, ROUTE-01, FWD-01, SHOWTOOL-01, EXPLORER-SESSION-01,
  PLACEHOLDER-01, BOUNDARY-01 source leg), H-NAV-A-01/02, H-NAV-B-01/02/03/04,
  H-NAV-C-01, H-NAV-D-01/02, H-NAV-E-01/02, H-NAV-F-01/02, H-NAV-G-01/02,
  H-ROUTE-SEL-01/02/03, H-ROUTE-PLAN-01; lane suites green as above
  (T01-01/T01-02/T02-02/T02-03 offline legs via lane A; T03-01/T03-05
  kernel-math legs; T06-01 partial + T06-03/T06-04/T07-01..06 offline legs
  via lane B bounds; T08-01/T08-04/T08-05 request-scope legs; T09-01/T09-04/
  T09-06 offline snapshot/marker-review legs via lane D; T10-02/T10-03
  offline policy/document-boundary legs via lane E; T11 offline legs via
  lane F; T12-04 planning-fence + T12-05 NoGo-refusal legs via lane G;
  ROUTE-01/02 offline legs).
- GLOBAL-02 runtime inventory: PASS (source leg — 10 wired + 2
  placeholders inventoried; C#-constructed PlacementPreviewPanel buttons
  stay a lane C/Engine-side fact; full runtime UI-Automation tree walk
  stays NOT_EXECUTED per environment below).
- GLOBAL-03 twelve destinations: 10 PASS (real task pages) + 2 honest
  placeholders (T04/T05) — gate stays OPEN, not passed, until T04/T05
  task views land.
- GLOBAL-04 action gating: PASS (offline legs — every lane reports
  per-action reasons + next steps; no gate bypassed to enable controls).
- GLOBAL-05 Engine-first boundary: PASS via boundary gate + H-BOUNDARY-01
  (17 files) on this head.
- GLOBAL-06 single shared session: PASS (source + offline legs —
  one Workbench construction, session retained, single-attach refusals,
  dispose teardown incl. ConstraintsDrcView).
- GLOBAL-08 offline authority: PASS (offline legs — archives/examples
  never authorize mutation; NoGo runner manufactures nothing).
- GLOBAL-09 mutation/recovery admission: PASS (offline legs —
  pre-dispatch refusal paths qualified; native legs NOT_EXECUTED).
- GLOBAL-10 retry/callback fencing: PASS (offline legs via lane suites).
- GLOBAL-14 diagnostics/cleanup: PASS (offline legs — bounded output,
  atomic writes, no shared-file deletion).
- GLOBAL-15 cross-tool stale data: PASS (offline legs — operation-bound
  receipts, epoch/supersede isolation, fingerprint/epoch invalidation
  wording).
- GLOBAL-16 evidence coverage: PASS (this handoff — missing/partial/
  unsupported stay explicit; no synthetic-only proof marked passed).
- GLOBAL-17 delivery completeness: PASS (this commit — matrices, logs,
  handoff committed on tools/h).
- NOT_EXECUTED (native/license gate — no Allegro license in this Linux
  container; lane H invokes no vendor executables directly): T04-04/05/06,
  T05-01..06 native legs, ROUTE native legs, T06/T07 live publication/
  capture/DPI legs, T09-02/03/05 native legs (effective reads, typed
  edits with readback/Undo, fresh DRC with completion + rule-scope
  evidence), T10-01..06 native legs (binding activation, 17-op positive
  fixtures, PACKAGE staging, preview/apply readback, DRA/PSM output),
  T11-01..06 native dispatch legs, T12-01/02/03/06 native legs
  (Artwork/ODB++/IPC-2581 generation + validation + promotion),
  GLOBAL-07 (fresh same-path reopen + Run-again with known geometry
  change), GLOBAL-11 (no-unwanted-Allegro-exit), GLOBAL-12 (final
  package native matrix), GLOBAL-18 (contention).
- NOT_EXECUTED (environment — Linux container, no WindowsDesktop
  runtime, no WPF execution): runtime UI-Automation tree walk (static
  XAML walk PASS as substitute here), package-only external consumer on
  exact final packages (needs coordinator release generation),
  performance p95/p99 budgets incl. T01-06/T02-06/T06-06/ROUTE latency
  (needs Windows fixtures + repeated sessions), `PD.DrawingChecks`.
- BLOCKED items: none beyond the license/environment gates above —
  every offline-qualifiable gate on the 7-lane surface passes; the
  final native campaign (disposable fixtures/copies only: fresh DRC
  with completion + rule-scope evidence, manufacturing export
  validation, readback, reopen/Run-again with a known geometry change,
  latency budgets) waits on the licensed slot + coordinator release
  generation.

## Branch-state observations for the coordinator (not qualified; flags only)

- All seven lanes now exist and are integrated at `aec6013`: A (`c076b25`),
  B (`9bf7459`), C (`0471762`), D (`6d3fda5`), E (`c53eb53`), F (`dee0196`),
  G (`04d1a32`). No lane branch is baseline anymore.
- `origin/tools/b` slice 2 edited coordinator-owned files
  (`MainWindow.xaml(.cs)`, `EngineExplorerView.xaml.cs`); coordinator has
  reconciled them at `42a87ab`. Lane H qualifies the integrated result only.
- T04/T05 remain section-forwarding targets (lane C fragments); dedicated
  PD task views for them are coordinator/integration work, still open.
- T09/T10/T12 pages activate fully only with the next Engine package
  generation (DrcRun/DrcReview + effective-read/mutation APIs, lane E
  binding, Engine-backed manufacturing binder); PD pins stay frozen at
  `1.13.0-preview.93` on every lane branch.
- Lane H runs no builds inside other lanes' worktrees (would dirty them).

## Limits / integration needs

- Native Allegro campaign: all seven lanes are integrated, so the
  remaining gate is the licensed slot + coordinator release generation.
  Shared fixture definitions follow kit plan section 20.3 (disposable
  copies only). Lane H runs no vendor executables directly — native
  behavior only via dotnet harnesses Lane H writes.
- UI-traversal runtime walk, package-only consumer on final pins, and
  latency/resource budgets are queued for Windows + coordinator release.
- Next Engine package must contain: `AllegroWorkspaceDrcRun` /
  `AllegroWorkspaceDrcReview` + constraint effective-read/mutation APIs
  (lane D), lane E public symbol binding (lane E), Engine-backed
  manufacturing exporter + IPC-2581 validator hook (lane G).
- Re-check cadence: origin tools/coordinator (+ both repos) every ~30 min;
  heartbeat commit+push to origin tools/h every 30 min; no force-push; never
  touch other branches.

## Addendum (2026-09-22, Muse simplification workstream)

### Pins

- Record-time pins (`.93`) are superseded: current pins are
  `1.13.0-preview.104` (`AllegroBridgePackageVersion` /
  `AllegroBridgeEnginePackageVersion` in `Directory.Build.props`).
- A parallel session has an in-flight, unpublished `.105` bump in the
  dirty worktree at the time of writing; it is not referenced by this
  workstream and is unverified here.

### Conformance rewrite (B2): behavior replaces source spelling

`tests/PD.ToolsConformance` no longer declares features complete from
XAML/code-behind text. Retired source-text checks (behavior moved as
noted, nothing preserved as spelling):

- Shell/section wiring → `PD.Simple.DrawingChecks`
  `ShellNavigationChecks` (live invisible-`MainWindow` click-through)
  and `ExplorerContractChecks` (attach/forwarding guards): retired
  `H-SHELL-NAV-01/02/03`, `H-SHELL-WIRED-01`, `H-SHELL-HDR-01`,
  `H-SHELL-CARD-01`, `H-SHELL-ROUTE-01`, `H-SHELL-FWD-01`,
  `H-SHELL-SHOWTOOL-01`, `H-NAV-A-01/02`, `H-NAV-B-01`, `H-NAV-C-01`,
  `H-NAV-D-01`, `H-NAV-E-01`, `H-NAV-F-01`, `H-NAV-G-01`,
  `H-EXPLORER-SESSION-01`, `H-PLACEHOLDER-01`.
- Hybrid checks keep their runtime halves only
  (`H-NAV-B-02/03/04`, `H-NAV-F-02/03`, `H-NAV-D-03`, `H-NAV-E-02/03`,
  `H-NAV-G-02/03`); `H-NAV-D-02` keeps the Engine capability-identity
  runtime check while its `.94` version-spelling assertions are
  deleted.
- New prioritized behavior checks: `H-CAT-SEL-01` (typed catalog rows,
  identity refresh, missing-clears) and `H-DRC-VAL-01` (scalar
  validation matrix plus preparation gating), both with hand-computed
  expectations and fixtures independent of `PD.Simple.Checks`.
- Kept: `H-BOUNDARY-01` (dependency/reflection architecture scan),
  `H-ROUTE-SEL-*`, `H-ROUTE-PLAN-*`.
- Suite result: 18/18 on Linux (`dotnet run --project
  tests/PD.ToolsConformance -c Release`).

### Defects remediated (before → after)

- A1 catalog selection: string-bound `PadDefinitionList` with
  `SelectedItem` casts and reset-on-refresh →
  `CatalogSelection` typed rows (`Name`/`Display`/`Detail`/`Usage` +
  `Summary`), selection preserved by definition identity across
  refresh, missing identity clears. (`CatalogRows.cs`,
  `CatalogSelectionChecks`.)
- A2 DRC input validation: unvalidated edit text reached preparation →
  `TryBuildScalar` per-kind validation (blank/malformed/valid ×
  numeric/boolean/symbol/text), `EditInputError` + `CanPrepareEdit`
  gating; invalid input never prepares. (`ConstraintsDrcChecks`.)
- A3 corridor findings: 200-finding cap with silent truncation →
  `DpViaCorridorFindingExplorer` over the complete set (totals,
  search, risk/layer filters, paging, identity selection); untruncated
  service + trimmed record + VM delegation; >200 reachability proven
  with the target beyond index 200 plus a small-result control.
  (`CorridorFindingsChecks`, `CorridorFindingsVmChecks`.)
- A4 run status: result status lived only inside the Setup popup →
  compact always-visible badge next to Run (Idle/Running/Success/
  Cancelled/Incomplete/Failed, distinct text + color + automation
  name), mid-run Cancel command, expandable detail surface; failure
  visible with Setup closed. (`RunStatusChecks` Linux + Windows.)
- B1 platform boundary: lower-SDK probes mixed into the consumer
  campaign → isolated `tests/PD.NativeCampaign/BridgePlatformProbe`
  with a documented gate exclusion; ordinary tree stays Engine-first
  (`check-engine-boundary.py`, `PD.EngineBoundaryChecks`, probe
  README).
- C1 catalog acquisition: dual full-board reads published without a
  freshness fence → audited family-scoped queries
  (`CatalogPublication`: padstacks 5 families, symbols 1 family),
  document-identity publication fence, and complete-coverage gate;
  delayed reads cannot publish an old catalog under a new connection.
  (`CatalogPublicationChecks` incl. scoped-vs-full fact parity.)
- C2 pair discovery: suffix-only `_P`/`_N` discovery presented as the
  whole truth → explicit `CorridorPairPolicy`: `SuffixCompat`
  preserved byte-for-byte, `DeclaredPairs` discovers Engine-declared
  pairs through Xnet membership, analyzes only unambiguous-polarity
  pairs, and reports incomplete/ambiguous declarations
  review-required. (`CorridorPairPolicyChecks`.)
- D ownership split: member-name switches over Engine enums (a rename
  breaks PD) → versioned `PhysicalSymbolOperationLabels` facade
  resolving by Engine numeric identity; renames flow, revalues fail
  loudly, future identities render fallbacks. Boundary recorded in
  `src/PD.PcbTools/PhysicalSymbolOwnership.md`.
  (`PhysicalSymbolFacadeChecks`.)

### Gate snapshot deltas

- GLOBAL-02 runtime tree walk: the static-XAML substitute is replaced
  by a live-tree walk plus click-through (`ShellNavigationChecks`) on
  Windows; full UI-Automation inventory still awaits the licensed
  Windows run.
- `H-BOUNDARY-01` retained; T04/T05 remain section-forwarding targets
  with no dedicated task views (GLOBAL-03 still open).
- E upstream verification: manufacturing, symbol-binding, and
  constraints/DRC live paths already call direct `.104` Engine APIs;
  the remaining `.94`-era work was stale gate/comment messaging,
  reframed to live-session + `.104` (see the rebind addendum).
