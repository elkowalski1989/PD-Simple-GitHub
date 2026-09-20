# Lane D handoff — T09 Constraints / DRC (PD-TOOLS-D)

## Source pair

- Bridge worktree `worktrees/bridge-d`, branch `tools/d`
  - Base: `da6aefc` (inherited fresh-execution slice, pushed)
  - Head: `30e5ea7` — "Lane D: repair inherited DRC fresh-execution compile and cover run-review contracts" (pushed to `origin tools/d`)
- PD worktree `worktrees/pd-d`, branch `tools/d`
  - Base: `fe26182` (clean, no prior Lane D work)
  - Head: this lane's commit (see below; to be pushed to `origin tools/d`)
- Frozen Engine package PD builds against: `1.13.0-preview.93`
  (built from bridge `ee0c748` plus local modifications per
  `packages/development-bundle.1.13.0-preview.93.json`).

No uncommitted work existed in either worktree at lane start; nothing was
redone. No other worktrees or branches were touched. No force-push.

## What was already there (preserved, not redone)

Bridge `da6aefc` supplied the fresh-execution chain: SDK
`AllegroPcbDrcRunContract` (`circuithub.bridge.internal.pcb.update-drc`,
full-board scope only), `AllegroWorkspaceDrcRun` (terminal run + fresh
marker capture, `engine.drc.execute` capability id), pure
`AllegroWorkspaceDrcReview` (group/filter/compare/export), and the SKILL
`chbPcbUpdateDrc` handler (`axlCNSMapUpdate` guarded by `isCallable`,
explicit unsupported path, registered in `pcb_drc.il` which
`PcbSkillPackage` embeds). Wire-id consistency verified:
`AllegroPcbContract.ExtensionId` is `circuithub.bridge.internal.pcb`, so the
composed command id matches the resident registration exactly.
`pcb_constraints.il` documents that `axlCNSMapUpdate` runs DRC and
deliberately avoids it, consistent with the new update command owning
execution.

## Bridge changes (this lane, commit 30e5ea7)

The inherited slice did not compile. Repairs, all in
`src/AllegroBridge.Engine/Live/`:

1. `AllegroWorkspaceDrcRun.cs`: added missing `Advanced` and `Scenes`
   usings (`EngineNativeContractMapper`, `EngineEvidence`,
   `EngineDiagnostic`).
2. `AllegroWorkspaceDrcReview.cs`: added missing `Scenes` using
   (`DataCompleteness`); rewrote `Group`, which misused the
   `GroupBy(keySelector, resultSelector)` overload (it compared the whole
   key tuple and the element sequence against strings and rescanned the
   full collection per group). It now counts per-group elements directly.

No behavior of the read path (`AllegroWorkspaceDrc`) was changed.

## Bridge tests (all offline, no Allegro)

Extended `tests/AllegroBridge.DrcChecks/Program.cs` (+~230 lines):

- Run-contract identity: update-drc wire id, scope, schema, full-board
  request scope, `engine.drc.execute` distinct from `engine.drc`.
- `ParseDrcRun` validation with hand-written JSON (independent oracle):
  completed × 4 freshness states, unsupported with reason, and rejection
  of wrong session, wrong board generation, unknown scope, unknown
  execution state, completed-with-detail, unsupported-without-reason,
  unsupported-with-freshness, oversized reason, control characters,
  missing/malformed/over-bound payloads, unknown execution name.
- `AllegroWorkspaceDrcReview`: grouping counts/ordering/null-layer,
  filtering, stable-key pairing (duplicates pair, waiver flips persist,
  adjacent-field splits do not collide), capture comparison
  (reorder/duplicate/added/removed), deterministic export, null guards.
- Execution-evidence identity: `WasExecutedForThisEvidence` is true only
  for Completed + fresh markers; Failed, markerless, or relabeled
  existing-marker reads are not fresh execution.

Results (Linux container, .NET 10 SDK):

- `dotnet run tests/AllegroBridge.DrcChecks` — PASS (baseline + new).
- `dotnet run tests/AllegroBridge.ConstraintAuthoringGate` — 19 passed.
- `dotnet run tests/AllegroBridge.ConstraintObservationGate` — 27 passed.
- `dotnet run tests/AllegroBridge.HostGate` — 982 assertions passed.
- `dotnet run tests/AllegroBridge.EngineGate` — all suites PASS.

## PD changes (this lane)

New tool page, exclusive Lane D ownership,
`src/PD.Simple/Tools/ConstraintsDrc/`:

- `ConstraintsDrcViewModel.cs` (WPF-free; linked into `PD.Simple.Checks`):
  offline-capable state over one shared `AllegroEngineSession`.
  Constraint snapshot observe (`Constraints.Read`) vs explicit refresh
  (`AcquireAsync`) with coverage honesty, fingerprint display, set/value/
  field/assignment/context-assignment/net-class/class-class browsing with
  domain/text filters; existing-marker review
  (`Drc.ReadAsync`) with type/layer/text/waived filters, group rows,
  selection with expected/actual inspection, PD-local review notes
  (explicitly not native waivers), capture comparison, deterministic
  PD-owned export text. Every action has an explicit gate with a visible
  reason. Effective-value reads, typed edits, and Run DRC are disabled
  with `PendingPackageReason`, which names the exact missing Engine APIs
  (`ReadEffectiveAsync`, mutation preparation, `AllegroWorkspaceDrcRun` /
  `AllegroWorkspaceDrcReview`) and the frozen package that lacks them.
  Reads never claim execution; capped lists say so.
- `ConstraintsDrcView.xaml` / `.xaml.cs`: thin view, stable
  `AutomationProperties.Name` on every action, `IsEnabled`/`ToolTip`
  bound to the VM gates. No session creation or disposal of shared
  owners. The XAML/codebehind are Windows-only and were authored, not
  compiled, in this Linux container (explicit limit below).
- `tests/PD.Simple.Checks/ConstraintsDrcChecks.cs` (+ link in
  `PD.Simple.Checks.csproj`, + call in `Program.cs`): 29 checks —
  disconnected gates with reasons, pending-package reasons, offline
  no-state-change, review/group/compare/export semantics, display
  honesty, null guards, double dispose.

Results:

- `dotnet run tests/PD.Simple.Checks` — PASS (37 + lifetime + route +
  29 new Constraints/DRC checks).
- `dotnet run tests/PD.PcbTools.Checks` — 123 PASS (no regressions).
- `PD.EngineBoundaryChecks` (needs `PD.Simple.dll`, WPF/Windows-only)
  could not run here; the VM uses only `Engine.Live/Design/Scenes`
  namespaces and the view references no SDK/transport types, so the
  boundary holds by construction pending Windows CI.

## Explicit limits / unresolved operations (doc refs)

- T09-05 fresh native execution: Engine/SDK/resident path is implemented
  and offline-tested, but never executed against licensed Allegro here
  (no vendor executables are invoked from this lane; none exists in this
  container). Still required: licensed run on a disposable board with a
  deliberate known violation (appears on run, disappears after repair and
  rerun), unsupported-version path, cancellation, lost receipt —
  TECHNICAL_IMPLEMENTATION_PLAN §13.3, ACCEPTANCE_MATRIX T09-05.
- T09-02/T09-03 typed edits with readback/Undo and fingerprint/epoch
  invalidation: Engine pipeline exists; positive native qualification
  still required (T09-02, T09-03).
- The `update-drc` resident command relies on `axlCNSMapUpdate`. It is
  guarded by `isCallable` with an explicit unsupported outcome, and its
  use is consistent with `pcb_constraints.il`, but native qualification
  of the call (completion semantics, DRC-state reporting) in the
  installed Cadence version is still open — plan §13.3.
- `PD.EngineBoundaryChecks`, WPF compile of the new view, and
  package-only consumer delivery run on Windows/on release hardware, not
  here.
- `tests/validate_resident_skill.py` is Python; this lane's shell is
  limited to `git`/`dotnet`, so it was not executed here — run in CI.

## Integration needs (coordinator-owned files; lane does not edit)

1. New Engine package generation containing bridge `tools/d` head
   (`AllegroWorkspaceDrcRun`, `AllegroWorkspaceDrcReview`,
   constraint effective-read/mutation APIs); PD pin stays
   `1.13.0-preview.93` in this lane.
2. Promote `AllegroPcbDrcRunContract.UpdateDrcCommand` into generated
   `AllegroPcbContract` (e.g. `UpdateDrcCommand`), plus a
   `PcbCommandCapabilities.Generated.cs` entry for the update-drc
   modify command.
3. Add `EngineCapabilities.DrcExecute` (`engine.drc.execute`) with a
   catalog specification requiring the update-drc command; suggested
   qualification `AcceptancePending` until T09-05 passes natively. The PD
   VM already gates Run DRC on the pending package; at integration, wire
   `CanRunDrc` to this capability and call
   `workspace.DrcRun.RunAsync(EngineDrcRunRequest.FullBoard)`.
4. Migrate PD's interim `ConstraintsDrcMarkerReview` to Engine
   `AllegroWorkspaceDrcReview` (identical comparison semantics), and wire
   effective reads/edits through `ReadEffectiveAsync` /
   `PrepareChangeAsync` with fingerprint/epoch repreparation.
5. MainWindow registration fragment (exact, coordinator to apply):
   - XAML: add `xmlns:constraintsDrc="clr-namespace:PD.Simple.Tools.ConstraintsDrc"`;
     replace the `Constraints / DRC · coming` FutureNavButton with
     `<Button x:Name="ConstraintsDrcMenuButton" Style="{StaticResource NavButton}" Click="ConstraintsDrc_Click">`
     with a `Constraints / DRC` label; add
     `<constraintsDrc:ConstraintsDrcView x:Name="ConstraintsDrcView" Visibility="Collapsed"/>`
     alongside the other tool hosts.
   - Codebehind: construct nothing new (view takes the session):
     `ConstraintsDrcView.AttachSession(_bridge.EngineSession);` after
     `ExplorerView.AttachPresentation(_presentation);`;
     `private void ConstraintsDrc_Click(object sender, RoutedEventArgs e) => ShowTool("constraintsdrc");`
     `ShowTool` gains `ConstraintsDrcView.Visibility = tool == "constraintsdrc" ? Visible : Collapsed;`
     disposal gains a `ConstraintsDrcView.Dispose()` block mirroring the
     corridor/Explorer blocks (view disposes only its VM, never the
     shared session).
