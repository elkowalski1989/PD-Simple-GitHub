# PD rebind handoff — Engine 1.13.0-preview.94 release integration (tools/rebind)

Date: 2026-09-20. Branch: `tools/rebind`. Base: coordinator `8667ba6`
(merged: staged .94 nupkgs + `packages/development-bundle.1.13.0-preview.94.json`,
bridge source `fec5beb`, all lanes A–G).

## Pin

- `Directory.Build.props`: `AllegroBridgePackageVersion` `1.13.0-preview.93` →
  `1.13.0-preview.94` (single pin; Engine version follows via
  `$(AllegroBridgePackageVersion)`). No other pin files touched.

## API evidence (every new Engine name verified against the .94 bridge sources
read-only at `worktrees/bridge-coordinator`, never invented)

- DRC: `AllegroWorkspaceDrcRun` + `RunAsync(EngineDrcRunRequest.FullBoard)`
  (`Live/AllegroWorkspaceDrcRun.cs`), `CapabilityId == "engine.drc.execute"`,
  `AllegroWorkspaceDrcRunResult` (`WasExecutedForThisEvidence`, `FreshMarkers`,
  `PostRunFreshness`, `ExecutedScope`, `Diagnostics`), pure
  `AllegroWorkspaceDrcReview` (`StableKey`/`Group`/`Filter`/`Compare`/`ExportLines`),
  `AllegroWorkspaceConstraints.ReadEffectiveAsync` / `PrepareChangeAsync` /
  `EnginePreparedConstraintChange.ExecuteToTerminalAsync` /
  `EngineConstraintMutationResult` (`IsVerifiedSuccess`, `Before`/`After`,
  `CanRecover`, `StartRecoveryAsync`) (`Live/AllegroWorkspaceConstraintAuthoring.cs`).
- Manufacturing: `AllegroWorkspaceManufacturing.ExecuteArtworkAsync` /
  `ExecuteIpc2581Async` / `ReportOdbPlusPlusHeadless`,
  `ManufacturingNativeToolset.RequireArtwork/RequireIpc2581Out/FindCadenceRootFromEnvironment`,
  `ProcessManufacturingNativeLauncher`, `ArtworkExecutionOptions`,
  `ArtworkParameterFiles`, `Ipc2581ExecutionOptions`, `Ipc2581ConfigurationFiles`,
  `ArtworkNativeExecution` / `Ipc2581NativeExecution` (`Result` + `Staging`).
- Symbols: `AllegroWorkspacePhysicalSymbols.ActivateAsync` →
  `EnginePhysicalSymbolBinding.PrepareAsync` →
  `EnginePhysicalSymbolPreparation.ApplyAsync(approvalIdentity)` with
  before/after readback, `EngineSymbolWorkArea.Plan`,
  `EnginePhysicalSymbolStaging.RequireStagedDocument`,
  `EnginePhysicalSymbolWorkflowValidator.ValidateRequest`,
  `EnginePhysicalSymbolPublisher.PublishAsync`,
  `EnginePhysicalSymbolExtensionDescriptor.RequireAccepted`.
- Padstacks: `EnginePadstackWorkflows` (`CheckAllEight`, `PlanGlobalEdit`,
  `PlanPurge`, `ParsePurgeReceipt`, `AssessDeletion`, `PlanRedefinition`,
  `RequestBoardPinReplacement`, `RequestSymbolPinReplacement`,
  `RequestCreateDefinition`), `EnginePadstackInspection` (`Stamp`,
  `InspectDefinition`, `InspectInstances`, `Compare`, `ExportDiagnostics`),
  `EnginePadstackLibrary` (`ValidateRequest`, `NativeWriteCall`).

## Rebinds done

- DRC view: Run wired to fresh native full-board execution with
  completion/freshness/rule-scope evidence (`WasExecutedForThisEvidence`
  required before accepting fresh markers; Unsupported/Failed reported with
  Engine detail, never as success); reads to effective facts (assigned vs
  effective reported, missing/conflicting/unsupported never substituted);
  typed edits (`BuildScalar`/`BuildChange` → prepare → execute-once →
  before/after readback → Engine recovery, never replay); refresh
  invalidates prepared edits; PD `ConstraintsDrcMarkerReview.Group`/`Compare`
  delegate to `AllegroWorkspaceDrcReview` (PD keeps row/export shapes; PD
  stable key asserted equal to the Engine key in tests). Marker reads never
  claim execution.
- Manufacturing: new `EngineManufacturingExportRunner` (lane-G fragment,
  verified) bound in `MainWindow` (`ManufacturingView.Runner = ...`),
  replacing the Unqualified default at runtime (the default stays as the
  unbound fallback). Promotion still needs `Complete` + held staging +
  not-consumed: manifest-exists is not success. Cancellation honored;
  license/install absence surfaces before any launch.
- Physical symbols: new `EngineSymbolBindingRunner` (shared workspace, no
  session ownership): pure `PlanStage`, descriptor-gated `ActivateAsync`,
  staged-doc cross-checked `PrepareAsync`, execute-once `ApplyAsync` with
  readback summary, completed-apply-only `PublishDraAsync` (PSM refused in
  Engine). View gained an Engine-binding card (stage/activate/preview/apply/
  publish with visible reasons); attached once in `MainWindow`.
- Padstacks: `PadstackTool` dispatches Engine inspection + workflow planners
  (catalog, definition/instance/compare/diagnostic export, global-edit,
  purge, targeted-delete assessment, receipt parsing, redefine-by-
  replacement, pending-native requests, library validation). Global purge is
  never presented as targeted delete; instances never as definitions. View
  gained an Engine-plans card (stamp-bound purge plan + targeted-delete
  refusal + diagnosis line).

## Gates (Windows dotnet 10.0.301, Release, this worktree)

- `PD.Simple` build: 0 warnings, 0 errors.
- `PD.Simple.Checks`: DRC slice 29 → 54, all PASS (review delegation
  semantics unchanged).
- `PD.PcbTools.Checks`: 140 → 154 PASS (Engine-binder refusal paths,
  headless ODB++ NoGo, cancellation, no-vendor-launch).
- `PD.ToolsConformance`: 31/31 → 35/35 (H-NAV-D-02 rewritten to the rebound
  contract, H-NAV-D-03/E-03/F-03/G-03 added; nothing weakened).
- `PD.ToolsB.Checks`: PASS. `PD.PadstackToolChecks`: 14 → 51 PASS.
  `PD.PhysicalSymbolToolChecks`: 72 → 92 PASS. `PD.EngineBoundaryChecks`: PASS.

## Remaining limits (not executed / not in this repo)

- Native qualification (licensed Allegro, disposable fixtures): T09-02/03/05
  effective/edits/fresh-DRC, T10-01..06 binding activation + apply + DRA
  publication, T11-01..06 native dispatch, T12-01/02/03/06 export + validation
  + promotion. The rebind compiles and gates the calls; execution proof waits
  on the licensed slot.
- Bridge-side follow-ups (Engine repo, not PD): promote
  `AllegroPcbDrcRunContract.UpdateDrcCommand` into generated
  `AllegroPcbContract`, and add an `engine.drc.execute` catalog specification
  (lane-D handoff items 2–3). PD gates Run on the `engine.drc` capability and
  reports the execution outcome honestly; the session catalog carries no
  separate execute authorization in .94.
- Non-positive/unbounded manufacturing timeouts surface as
  `ArgumentOutOfRangeException` from the Engine boundary and appear as
  action-level generation failures in the view (handoff note: no silent default).
- No force-push; `main`, `tools/coordinator`, lane branches untouched. Test
  adapters remain dev-only console harnesses.
