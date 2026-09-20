# PD Tools lane H handoff (PD repo) — independent conformance + qualification

Date: 2026-09-20. Lane: H (all tools, conformance only). Branch: `tools/h`.
Worker identity for git: lane-h (per-command `-c` flags; no global config change).

## Base / head

- Base (frozen pair): PD `fe26182ebfea8ad0b81ce5a0bce49e280b1198c2`.
- tools/h tracks tools/coordinator head `0871903` (coordinator prep `356bb10`
  + OpenSection forwarding `7b4341a` + lane C integration `0871903`).
- This commit: durable conformance harness + first qualification slice + evidence.
  No production source touched; no MainWindow/registry/pins touched.

## What changed (H-owned files only)

1. `tests/PD.ToolsConformance/PD.ToolsConformance.csproj` + `Program.cs`
   (scaffold existed untracked from prep; corrected and extended here):
   - Source-conformance checks parse the actual checkout text
     (`src/PD.Simple/MainWindow.xaml`, `MainWindow.xaml.cs`,
     `Engine/EngineExplorerView.xaml.cs`).
   - Runtime checks use hand-computed expectations against the frozen
     `EngineHorizontalFirstRoutePolicy` API; never values copied from the
     implementation under test.
   - Two prep-scaffold checks were over-strict and corrected with evidence:
     `H-SHELL-NAV-02` now asserts one sidebar button `Content=` per tool label
     (three labels honestly repeat in a home-page placeholder notice —
     product behavior, not a defect); `H-SHELL-FWD-01` no-reflection probe now
     matches real reflection markers (`System.Reflection`, `GetField(`,
     `GetMethod(`, `BindingFlags`, `MakeGenericMethod`) instead of flagging
     plain `?.Invoke` event raisings (x2, legitimate).
   - Added `H-SHELL-SHOWTOOL-01` (single `ShowTool(string?)` dispatcher, all
     four preserved keys) and `H-EXPLORER-SESSION-01` (exactly one shared
     `EngineWorkbenchView`, session-retention check, detach-on-dispose,
     double-attach refusal).
   - Evidence writes to `evidence/lane-h/conformance-<stamp>.csv` plus a
     stable `evidence/lane-h/conformance-latest.csv` inside this worktree so
     results commit on `tools/h` (overridable via `LANE_H_EVIDENCE_DIR`).
2. `docs/handoffs/PD-TOOLS-H.md` (this file).
3. `evidence/lane-h/` run CSVs.

## Controls covered (integrated head 0871903)

- NAV-HOME/EXPLORER/DPVC/ROUTE, HDR-ATTACH/SCREENSHOT, CARD-EXPLORER/DPVC/ROUTE,
  ROUTE-01..04, NAV-T01..T12 placeholder inventory (12 FutureNavButtons, style
  still gates `IsEnabled=False`), OpenSection forwarding contract,
  single-shared-session construction, ROUTE layer-selection policy
  (`SelectLayer`: single-active, first-visible fallback, invisible-active
  ignored, ambiguous/no-visible/empty/null rejections) and H-first plan
  geometry (hand-computed bend, net-conflict unassignment, width bounds).

## Tests (Windows dotnet 10.0.301+, Release) — all on tools/h @ 0871903

- `tests/PD.ToolsConformance`: PASS 14/14.
- `tests/PD.PcbTools.Checks` (independent re-run of integrated lane C):
  PASS 127.
- `tests/PD.Simple.Checks`: PASS 37.
- `tests/PD.EngineBoundaryChecks`: PASS (zero lower-SDK deps; supports
  GLOBAL-05 Engine-first boundary on this head).
- `dotnet build src/PD.Simple/PD.Simple.csproj`: 0 warnings/errors.
- Full log: `evidence/lane-h/conformance-latest.csv` (+ stamped run CSV).

## Gate snapshot (kit ACCEPTANCE_MATRIX.csv; integrated head only)

- PASS (source/runtime, offline): H-SHELL-NAV-01/02/03, H-SHELL-WIRED-01,
  H-SHELL-HDR-01, H-SHELL-CARD-01, H-SHELL-ROUTE-01, H-SHELL-FWD-01,
  H-SHELL-SHOWTOOL-01, H-EXPLORER-SESSION-01, H-ROUTE-SEL-01/02/03,
  H-ROUTE-PLAN-01; lane-C suites green as above.
- NOT_EXECUTED (all native gates: licensed slot gate — lane branches
  tools/a..g show no pushed final handoffs and tools/coordinator holds 1 of 7
  integrations; per lane brief, no native operations until all seven
  integrations land): T04-04/05/06, T05-01..06 native legs, ROUTE-01/02 native
  legs, GLOBAL-07/11/12/18, T09/T10/T11/T12 full matrices.
- NOT_EXECUTED (not yet integrated): T01/T02/T08 (no tools/a branch),
  T03/T06/T07 (tools/b pushed slices 1+2, not integrated), T09 (no tools/d
  content — origin/tools/d is baseline), T10 (no tools/e branch), T11
  (tools/f pushed, not integrated), T12 (no tools/g branch).

## Branch-state observations for the coordinator (not qualified; flags only)

- `origin/tools/b` slice 2 (`ffb6753`) edits coordinator-owned files
  (`MainWindow.xaml`, `MainWindow.xaml.cs`, `EngineExplorerView.xaml.cs`) and
  performs sidebar wiring — needs reconciliation at integration; lane H will
  qualify T03/T06/T07 only after the coordinator integrates.
- `origin/tools/f` (`5c42691`) preserves inherited padstack work including
  `src/PD.PcbTools/PadstackTool.cs` + `tests/PD.PadstackToolChecks`; Engine-
  first boundary (GLOBAL-05) re-check queued for integration.
- Lane H runs no builds inside other lanes' worktrees (would dirty them);
  non-integrated branches get source-level review only until integrated.

## Limits / integration needs

- Native Allegro campaign blocked on the license gate above; shared fixture
  definitions to follow the kit plan section 20.3 (disposable copies only).
- UI-traversal (runtime Automation tree walk per plan 20.1) and package-only
  consumer checks are scaffolded next, gated on further integrations.
- Re-check cadence: origin tools/coordinator (+ both repos) every ~30 min;
  heartbeat commit+push to origin tools/h every 30 min; no force-push.
