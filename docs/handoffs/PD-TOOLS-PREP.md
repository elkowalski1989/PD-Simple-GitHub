# PD Tools preparation record (coordinator)

Date: 2026-09-19/20. Kit: PD_Simple_All_Buttons_Implementation_Kit (audited 2026-09-19).

## Immutable source pair

- Bridge: `2fa3feb19dcbcd8ae06299c14543bf648cb8b4ff` (local main clean = origin HEAD = audited baseline).
- PD: `fe26182ebfea8ad0b81ce5a0bce49e280b1198c2` (fresh clone = origin HEAD = audited baseline).
- Package pin: `1.13.0-preview.93`.
- No newer work existed in either repo; nothing reconciled, nothing reset.
- Unrelated: `/mnt/c/e2studio/PD` (remote `elkowalski1989/PD`, different repo) is NOT in scope; do not touch.
- Desktop `boards/PD-Simple` is a git-less export copy; not authoritative.

## Frozen shared contracts

1. Engine public SDK surface as of Bridge 2fa3feb is the application interface. Lanes consume it; additive public APIs only, inside the lane's owned Engine subtree, declared in the lane handoff.
2. PD package pins, `MainWindow.*`, central tool registry, generated shared command registries: coordinator-only. Lanes return registration fragments as explicit proposed patches; they never edit these files.
3. `ToolDescriptor` / availability / operation-result shapes follow plan sections 4.2–4.4; reuse existing Engine evidence/recovery types first.
4. Lane test adapters are development-only and never close final native gates.
5. No invented native API names. Unverified native surface stays explicitly unresolved with evidence.
6. Fast Browse guarantee, fresh Run semantics, operation-bound receipts, debug-images-off default: preserved by every lane.

## Ownership and worktrees

| Owner | PD worktree (branch) | Bridge worktree (branch) | Exclusive paths |
|---|---|---|---|
| Coordinator | `worktrees/pd-coordinator` (`tools/coordinator`) | `worktrees/bridge-coordinator` (`tools/coordinator`) | shell, registry, pins, integration, this doc |
| A T01/T02/T08 | `worktrees/pd-a` (`tools/a`) | — | PD Tools/Analysis, Tools/Captures, query/archive adapters+tests |
| B T03/T06/T07 | `worktrees/pd-b` (`tools/b`) | — | PD Tools/Interaction, Tools/Review, presentation adapters+tests |
| C T04/T05+ROUTE | `worktrees/pd-c` (`tools/c`) | `worktrees/bridge-c` (`tools/c`) | PD Tools/Placement, Tools/CopperEditing; Engine editing/routing + native edit modules |
| D T09 | `worktrees/pd-d` (`tools/d`) | `worktrees/bridge-d` (`tools/d`) | PD Tools/ConstraintsDrc; Engine constraint/DRC + native owners |
| E T10 | `worktrees/pd-e` (`tools/e`) | `worktrees/bridge-e` (`tools/e`) | PD Tools/PhysicalSymbols; Engine symbol binding/intents + extension |
| F T11 | `worktrees/pd-f` (`tools/f`) | `worktrees/bridge-f` (`tools/f`) | PD Tools/Padstacks; Engine padstack/native owners |
| G T12 | `worktrees/pd-g` (`tools/g`) | `worktrees/bridge-g` (`tools/g`) | PD Tools/Manufacturing; Engine Manufacturing subtree + format adapters |
| H all tools | `worktrees/pd-h` (`tools/h`) | (on request) | new independent test/harness/fixture/evidence files only |

All worktrees rooted at `/mnt/c/e2studio/worktrees/`. Lane evidence roots (outside repos): `/mnt/c/e2studio/_lanes/<lane>/`.
Lane handoff: `docs/handoffs/PD-TOOLS-<LANE>.md` committed on the lane branch.

## Native-resource schedule

Single licensed Allegro; no concurrent same-board manipulation. Each lane works from its own disposable fixture copy.
Live-Allegro qualification slots are granted by the coordinator on request, one lane at a time; H's shared fixture
definitions are the reference inputs. Never replace another lane's Host/resident.

## .93 evidence recovery

Preserved in place (not moved, not regenerated):
`/mnt/c/e2studio/pd-simple-local-runs/preview93-campaign/` — browse-campaign.log, finish-sweep.log,
finish-evidence-20260919.md, docswitch*.log, strict-clean.log, stage-line.txt, follow-initial.txt.
Final commit/package attestation will be appended at integration.

## Coordinator first delivery (in progress)

Thin PD `OpenSection` forwarding over the existing shared EngineWorkbenchView + real navigation registration
for existing sections, with precise disabled-action reasons. Not a declaration that tools are finished.
