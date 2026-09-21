# PD Tools lane C handoff (PD repo) — T04/T05 + ROUTE (PD side)

Date: 2026-09-20. Lane: C (T04/T05 + existing ROUTE). Branch: `tools/c`.
Worker identity for git: `lane-c <lane-c@local>` (per-command `-c` flags).

## Base / head

- Base (frozen pair): PD `fe26182ebfea8ad0b81ce5a0bce49e280b1198c2`.
- Head: this commit on `tools/c` (see `git log`; only `tools/c` pushed, no force).
- Engine work is in the sibling `tools/c` branch of Bridge (`PD-TOOLS-C.md` there).

## What changed (PD only; no MainWindow/registry/pins touched)

1. `tests/PD.PcbTools.Checks/Program.cs`
   - New `EngineHorizontalFirstRoutePolicy.SelectLayer` qualification:
     single active visible layer selected; fallback to first visible with none
     active; multiple active visible layers rejected (`InvalidDataException`);
     no visible layer rejected (`InvalidOperationException`).
   - Existing H-first truncation-bucket, negative-coordinate, metric, width,
     net-conflict, and unknown-units cases untouched and green.
2. `docs/handoffs/PD-TOOLS-C.md` (this file).

No production PD source changed: ROUTE flow (`BridgeSession.EngineRoute`,
`InteractiveRouteCompletion`, `EngineHorizontalFirstRoutePolicy`) is preserved
exactly as the named limited H-first mode with explicit net-conflict behavior.
PD consumes Engine through the frozen pinned packages, so PD builds/tests are
independent of the Bridge lane branch.

## Controls covered

- ROUTE-01..04 (two-pick flow, clear first pick, cancel, undo + net policy):
  preserved, qualified offline (see tests). No handler changes.
- NAV-T04 / NAV-T05: registration fragments proposed below for the coordinator;
  no PD registry/MainWindow edits made by this lane.

## Tests (Windows dotnet 10.0.301, Release)

- `tests/PD.PcbTools.Checks`: PASS 127 (route policy incl. new SelectLayer cases).
- `tests/PD.Simple.Checks`: PASS (route completion keeps Engine terminal
  evidence primary; connection/busy/recovery lines green).
- `tests/PD.EngineBoundaryChecks`: PASS (no lower-SDK dependency drift).
- `dotnet build src/PD.Simple/PD.Simple.csproj`: succeeded, 0 warnings/errors.
- Full log: `_lanes/c/test-log.txt`. Acceptance delta: `_lanes/c/acceptance-delta.csv`.
- Native gates: NOT_EXECUTED (licensed slot with the coordinator).

## Proposed registration fragments (coordinator owns the registry)

```csharp
// T04 Placement handles -> existing Placement section (offline-capable page).
OpenSection(WorkbenchSection.Placement);
// T05 Via / route editing -> existing NativeEdits section + RoutePreview.
OpenSection(WorkbenchSection.NativeEdits);
OpenSection(WorkbenchSection.RoutePreview); // local candidate + approved-plan review
```

Suggested availability: pages openable disconnected (offline/preview mode with
setup guidance); Apply-class actions keep the Workbench's live-scene,
fresh-capture, approval, license, and recovery gates. ROUTE entry keeps its
current handler, H-first wording, and net-conflict behavior unchanged.

## Limits / integration needs

- T04/T05 PD task views beyond section forwarding (if wanted) are coordinator/
  integration work; Engine previews, handles, and approval models are ready and
  tested (see Bridge handoff).
- Native qualification (placement batch, via/path edits, reshape/multilayer
  apply, ROUTE two-pick) waits on the licensed slot with disposable fixtures
  (`_lanes/c/fixture-definitions.json`); all native gates stay NOT_EXECUTED.
