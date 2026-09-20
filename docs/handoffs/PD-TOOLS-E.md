# PD Tools lane E handoff (PD repo) — T10 Physical symbols (PD side)

Date: 2026-09-20. Lane: E (T10). Branch: `tools/e`.
Worker identity for git: lane-e (per-command `-c` flags; no global config change).

## Base / head

- Base (frozen pair): PD `fe26182ebfea8ad0b81ce5a0bce49e280b1198c2`.
- Head: this commit on `tools/e` (see `git log`; only `tools/e` pushed, no force).
- Engine work is the sibling `tools/e` branch of Bridge, prior slice
  `23d556c` (public activation, staged work area, DRA publication, 17-op
  qualification), continued without redo: no new Bridge edits in this slice.
- PD consumes Engine through the frozen pinned packages
  (`1.13.0-preview.93`), so PD builds/tests are independent of the Bridge lane
  branch.

## What changed (PD only; no MainWindow/registry/pins touched)

1. `src/PD.PcbTools/PhysicalSymbolTool.cs` (new)
   - T10 task policy over the frozen Engine contracts only
     (`EnginePhysicalSymbolOperation`, `EnginePhysicalSymbolCapabilities`).
     No session, no Host, no file staging, no native authority manufactured.
   - 21 actions: stage work area, inspect definitions, verify source, one per
     each of the 17 source-backed candidate operations, publish DRA.
   - Page opens disconnected: captured definition/pin inspection runs offline
     on the current board capture. Every other action carries its exact Engine
     capability reason plus a next step; nothing silently enables.
   - Per-operation reasons equal the Engine capability truth verbatim
     (asserted in tests); no native command identifiers leak (asserted: no
     `.v1` / `physical-symbol.` tokens).
   - `VendorLimits`: PSM refusal with the Cadence SPB_25.1 axlCompileSymbol
     doc ref, empty production-supported list, board-instance boundary.
2. `src/PD.Simple/Tools/PhysicalSymbols/PhysicalSymbolsView.xaml` (+ `.xaml.cs`, new)
   - Task view mirroring the lane F view shape: title, mode line, staged
     PACKAGE work-area card, captured-definitions card, candidate-operations
     card, vendor-limits card. Stable `PhysicalSymbols.*` automation IDs.
   - `ShowScene(sceneOrNull, isLiveConnected)` + `StageSymbol(nameOrNull)`;
     owns no session and starts no native operation.
3. `src/PD.Simple/Tools/PhysicalSymbols/RegistrationFragment.md` (new)
   - Coordinator proposal: `tools.physical-symbols` descriptor, T10 sidebar
     replacement, and the integration rebinding list (descriptor activation,
     `EngineSymbolWorkArea`, binding preview/apply + readback, publisher)
     with the Engine pin raise. PSM stays refused.
4. `tests/PD.PhysicalSymbolToolChecks/` (new console harness)
   - Registration, 17-op inventory vs the Engine enum, reason-equals-truth
     for every op, no command-ID leak, empty production list, all-groups
     coverage, offline four-pad fixture (`CASE_QFP` 1-4 + `PKG_EMPTY`),
     disconnected/live/staged gating, publish approval + PSM limit.
5. This file.

## Controls covered

- NAV-T10: registration fragment proposed for the coordinator; no PD
  registry/MainWindow edits made by this lane.
- T10-01 (public activation): PD side consumes only public Engine capability
  truth; the activation binding itself ships on Bridge `tools/e`.
- T10-02 (candidate operations): all 17 mapped to task actions with exact
  pending reasons; production list honestly stays empty.
- T10-03 (document/source): staged-PACKAGE vs board-instance boundary in
  stage/verify reasons; source verification itself waits on the binding.
- T10-04 (preview/apply): preview-non-mutating / accepted-apply / readback
  wording staged in the view; execution waits on the binding.
- T10-05 (DRA/PSM publication): publish action gated on completed apply
  evidence + approval + destination policy; PSM refused with vendor doc ref.
- T10-06 (failure/recovery): wrong-document, stale-binding, numbering, and
  overwrite-refusal behavior documented via Engine reasons; live proof waits
  on the licensed slot.

## Tests

- `tests/PD.PhysicalSymbolToolChecks`: PASS (console harness; see command
  below). Native gates: NOT_EXECUTED (licensed slot with the coordinator).
- `dotnet run` on the new harness exercises only managed policy + the frozen
  Engine package; no Allegro, no Host, no filesystem staging.

## Limits / integration needs

- No live Allegro on this machine: T10-01..T10-06 native gates stay
  NOT_EXECUTED. The Bridge `tools/e` qualification checks are synthetic-
  fixture Engine-workflow proof, not vendor acceptance.
- Integration rebinds the view/policy to the new lane E Engine APIs and
  raises the Engine pin past 1.13.0-preview.93 (see RegistrationFragment).
- PSM compilation is an explicit unresolved operation
  (`library_compile_contract_insufficient`, axlCompileSymbol doc ref); DRA
  publication proceeds without PSM output.
- WPF view compile/run needs Windows; Linux verification covers the policy
  project + checks harness only.
