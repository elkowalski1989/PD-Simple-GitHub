# PD-TOOLS-F — Lane F (T11 Padstacks) handoff

## Source pair (immutable)
- Bridge base `2fa3feb19dcbcd8ae06299c14543bf648cb8b4ff`, inherited head
  `6d5c008` ("Lane F: preserve inherited padstack work"), plus this session's
  scratch-hygiene fix on top.
- PD base `fe26182ebfea8ad0b81ce5a0bce49e280b1198c2`, inherited head
  `5c42691` ("Lane F: preserve inherited padstack tool work"), plus this file.
- Engine package pin `1.13.0-preview.93` exact (`Directory.Build.props`;
  `[$(AllegroBridgeEnginePackageVersion)]` in `PD.PcbTools`/`PD.Simple`).
- Branch `tools/f` in both repos; worktrees `worktrees/bridge-f`, `worktrees/pd-f`.
  No other worktree/branch touched. No force-push.

## What was inherited (preserved, not redone)
Bridge `6d5c008`: `EnginePadstackInspection` (definition/instance/compare/
where-used/diagnostic export over immutable captures), `EnginePadstackWorkflows`
(8-row catalog check, global-edit planner, purge planner, board-replacement
planner, pending-native requests, redefine-by-replacement), `EnginePadstackLibrary`
(staging/publication contract), `Skill/Pcb/pcb_padstacks.il` native owners wired
into `PcbSkillPackage`, `tests/AllegroBridge.PadstackGate` gate.
PD `5c42691`: `PD.PcbTools/PadstackTool` (11 actions, offline summaries, gates),
`PD.Simple/Tools/Padstacks/PadstacksView` (opens disconnected, session-free),
`RegistrationFragment.md` coordinator proposal, `tests/PD.PadstackToolChecks`.

## This session's changes
- Bridge: gate scratch moved from `Path.GetTempPath()` to a worktree-local
  `lane-f-scratch` dir under `AppContext.BaseDirectory` (lane approval hygiene;
  still cleaned up in `finally`). No behavior change.
- PD: this handoff file.

## Public contracts (Bridge, `CircuitHub.AllegroBridge.Engine.Live`)
- `EnginePadstackInspection.{Stamp,InspectDefinition,InspectInstances,Compare,ExportDiagnostics}`
- `EnginePadstackWorkflows.{CheckAllEight,Check,PlanGlobalEdit,PlanPurge,ParsePurgeReceipt,AssessDeletion,PlanBoardReplacement,RequestBoardPinReplacement,RequestSymbolPinReplacement,RequestCreateDefinition,PlanRedefinition,AttributeRules}`
- `EnginePadstackLibrary.{NativeWriteCall,ValidateRequest,BeginStaging,AdmitArtifact,Promote,Abandon}`
- Skill owners in `pcb_padstacks.il`: `chbPadstackUpdateGlobals` (axlPadstackEdit),
  `chbPadstackPurgeUnused` (axlPurgePadstacks, global only), `chbPadstackWriteLibrary`
  (axlPadstackToDisk, explicit name), `chbPadstackDescribe` (presence probe).
- PD: `PadstackTool.{Registration,SummarizeDefinitions,DescribeActions,UnsupportedOperations}`,
  `PadstacksView.ShowScene(scene, isLiveConnected)`.

## Controls / gates
Page opens disconnected with offline mode line; every action carries
`Available/Reason/NextStep`. Live mutations require capture + live session;
create/symbol-pin wait on lane E acceptance; global edit/purge/library wait on the
licensed dispatch slot; layer edit and targeted delete are disclosed unsupported.

## Tests (this session, observed)
- `dotnet run --project tests/AllegroBridge.PadstackGate/AllegroBridge.PadstackGate.csproj`
  → passed, 86 assertions (T11-01…T11-06 + catalog).
- `dotnet run --project tests/PD.PadstackToolChecks/PD.PadstackToolChecks.csproj`
  → passed, 14 checks.
- `dotnet build src/AllegroBridge.Host/AllegroBridge.Host.csproj` → 0 warn, 0 err.
- `dotnet build src/PD.Simple/PD.Simple.csproj` → 0 warn, 0 err.
- Native gates T11-01…T11-06 remain NOT_EXECUTED: no licensed Allegro runtime in
  this container; `.il` owners and dispatch are implemented but unqualified.

## Limits / unresolved (with refs)
- `UpdateLayerGeometry`: unsupported — Cadence 25.1 `axlPadstackEdit` does not edit
  pad layer characteristics (code `padstack_layer_update_unsupported`). Honest
  alternative: redefine-by-replacement (`PlanRedefinition`).
- `DeleteDefinition`: no documented target-specific delete API
  (`padstack_targeted_delete_unsupported`). Global purge (`axlPurgePadstacks`) is
  never presented as targeted delete.
- Board-pin replacement: documented API, no packaged owner
  (`board_pin_replace_native_owner_missing`) — validated request, explicitly held.
- Create/symbol-pin: candidate-checked, pending lane E production acceptance.
- Shared persistence: file ownership separated per plan §15.2 (lane F owns PAD
  staging/publication; lane E owns symbol extension files); shared filesystem
  helpers need one coordinator owner at integration.

## Integration needs (coordinator)
Apply `src/PD.Simple/Tools/Padstacks/RegistrationFragment.md` (registry entry,
sidebar button replacing the T11 placeholder, Engine pin raise); rebind the view
to the new Engine APIs; qualify native dispatch + `.il` owners in a licensed slot;
run the disposable-fixture native gates T11-01…T11-06.
