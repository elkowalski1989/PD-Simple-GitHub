# Astra Handoff — Engine/PD Boundary Closure (RC 1.13.0-preview.85)

Date: 2026-09-17. Goal complete, all gates green, live-verified on `ingram9z_040324.brd`.

## Commits under review

| Repo | Commit | Content |
|---|---|---|
| allegro-bridge | `b4bc2e1` | Engine boundary work, phases 1–4 (18 files) |
| PD-Simple-GitHub | `05b5a9d` | PD migration + pins + RC record (29 files) |
| PD-Simple-GitHub | (this file) | Handoff |

## What changed and why

No architecture rewrites. Corridor interpretation stays in PD; the following moved
into Engine ownership with explicit contracts:

1. **Witness mapping (Engine-owned, no native-index leak)** — `src/AllegroBridge.Engine/Live/EngineWitnessMatching.cs`
   (new). PD matches witnesses through Engine handles with explicit verification
   guarantees; raw native indices no longer cross the boundary.
2. **Typed native diagnostics** — `NativeBusyException` (Host), `AllegroNativeBusyException` (Sdk),
   `SurfaceUnavailableEvidence` (Engine/Scenes), consumed via `Live/EngineNativeRetry.cs` (new).
   Busy / unavailable-surface / reconnect are distinct typed failures, not strings.
3. **Split drawing/publication lifecycle with receipts** — drawing and publication complete
   independently; each produces a completion receipt PD can reconcile. Overlay path
   (`EngineWpfLiveOverlay.cs`) covered; no overlay regressions (WpfGate 188 green).
4. **Qualified curve conversions with provenance** — `EngineWpfPresentation.cs`. Every converted
   curve carries its qualification + provenance; unqualified topology is reported, not silently drawn.
5. **Selection-path performance (measured, no change justified)** — 0 ms live, 22 ms at 30k
   synthetic load. No managed-code change; measurement only.
6. **RC record** — `_local-runs/rc-1.13.0-preview.85.json` (force-added; dir is otherwise ignored).
   Ties source, packages, loaded binaries, Host, board, and results.

PD side (`05b5a9d`): corridor (`CorridorNavigation.cs`, `DpViaCorridorWorkspaceViewModel.cs`,
`EngineDpViaCorridorService.cs`) + overlay (`BoardOverlayController.cs`) migrated to the new
contracts; pins moved to 1.13.0-preview.85 (`Directory.Build.props`, both csproj files);
`tests/PD.PcbTools.Checks/Program.cs` updated. All four package sets (.82–.85, Engine/Core/Sdk/Wpf)
plus bundle manifests vendored under `packages/` per repo convention.

## Verification evidence

| Check | Result |
|---|---|
| EngineGate | 454 green |
| SecurityGate | 507 green |
| WpfGate | 188 green |
| HostGate | 978 green |
| PD checks (`tests/PD.PcbTools.Checks`) | 119 green |
| Live GUI on ingram9z_040324.brd (.82 → .85) | pass each rev; board stays open, recording on proceed-yes |
| Fixed en route | WpfGate revision-order failure; SecurityGate name clash; EngineGate nullability; Allegro uprev dialog blocking attach (closed via UI automation) |

Primary evidence: the RC record above + gate projects under `tests/` in allegro-bridge at `b4bc2e1`.

## Boundary contract (post-change)

- Engine owns: witness matching, native diagnostics taxonomy, draw/publish lifecycle + receipts,
  curve qualification + provenance.
- PD owns: corridor interpretation, overlay presentation, check orchestration.
- Crossing the boundary: Engine handles + typed exceptions + receipts. No native indices, no stringly failures.

## Notes / residual items

- Compat: the old int-based overload was retained alongside the new handle API — flag if you want
  it removed or formally deprecated.
- Out-of-repo artifacts (not committed): live screenshots under the user's Pictures dir, witness
  probe logs under `Desktop/boards/_local-runs/witness82-probes/`. Paths are referenced from the
  RC record; say the word if copies should be vendored.
- Overlay received explicit regression attention per operator constraint; WpfGate 188 + live GUI
  pass are the guards.
