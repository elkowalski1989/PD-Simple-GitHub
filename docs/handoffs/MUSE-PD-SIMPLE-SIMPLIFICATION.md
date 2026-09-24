# Muse handoff: make PD Simple a genuinely simple Engine consumer

## Purpose

Use this document as the complete task prompt for a Muse console session.

Implement the PD Simple audit remediation described below in:

- Windows: `C:\Users\EMILEKOWALSKI\Desktop\boards\PD-Simple-GitHub`
- WSL: `/mnt/c/Users/EMILEKOWALSKI/Desktop/boards/PD-Simple-GitHub`

The product intent is non-negotiable: Allegro Engine owns reusable platform,
session, native-operation, typed-data, and drawing behavior. `PD.PcbTools` owns
PCB engineering policy. `PD.Simple` should be a small, understandable product
composition and presentation layer that demonstrates how to build on Engine.

Do not work on the Nisqually large-board resource-bound failure. That work is
reserved for Codex/Astra and is specified separately in
`NISQUALLY-LARGE-BOARD-HANDOFF.md`.

## Repository and safety boundaries

The worktree is intentionally dirty. Inspect `git status --short` and the actual
diff before editing. Preserve all existing user/Codex changes, especially the
hidden startup, activation/reconnect, compact-window, and corridor-layout work.
Do not reset, revert, delete, commit, push, rebase, or rewrite unrelated work.

Use the pinned Preview.104 source and package contract as authority:

- `Directory.Build.props` pins `1.13.0-preview.104`.
- Adjacent Engine source, when needed for API inspection, is
  `/mnt/c/e2studio/allegro-bridge`.
- PD production projects must consume Engine/WPF packages rather than adding a
  lower SDK reference.
- Do not move PD-specific corridor/routing policy into Engine.
- Do not hide incomplete data, uncertain mutations, or failed native operations
  behind successful-looking UI.
- Do not change the board files.

Before implementation, restate the observable outcome and inspect the current
versions of every named file. Source line numbers below describe the audited
worktree and may move.

## Required outcome

PD Simple must become easier for both an end user and a feature author:

1. Typed Engine data remains typed through presentation.
2. Normal invalid input produces inline validation, never a dispatcher crash.
3. All acquired corridor findings are reachable through search/filter/paging.
4. Operation failures are visible without opening a configuration popup.
5. Production/test/sample architecture claims match reality.
6. Documentation describes Preview.104 and the current source tree.
7. Feature behavior is tested through public/model/UI behavior rather than
   source-text spelling.
8. Application coordination is split by responsibility without moving product
   policy into Engine or disturbing the existing startup/reconnect work.
9. Engine data acquisition is scoped and freshness-fenced before publication.
10. Declared differential-pair support is added only as an explicit product
    policy, without inventing P/N polarity from generic Side A/B metadata.

## Workstream A: fix concrete user-facing defects first

### A1. Preserve typed catalog rows

Current defect:

- `src/PD.Simple/Tools/Padstacks/PadstacksView.xaml.cs` binds formatted strings
  to `PadDefinitionList`, then casts `SelectedItem` to
  `PadstackDefinitionSummary`.
- `src/PD.Simple/Tools/PhysicalSymbols/PhysicalSymbolsView.xaml.cs` does the
  same with `PhysicalSymbolDefinitionSummary`.
- Both rerender and replace `ItemsSource` from `SelectionChanged`.

Required implementation:

- Bind the typed summary objects.
- Format rows with a data template, converter, or a stable typed display model.
- Preserve selection across refreshes by stable definition identity.
- Do not recursively reset `ItemsSource` from selection changes.
- Ensure details, usage, staging/planning, and action availability follow the
  actual selected typed row.

Required proof:

- A behavior test selects a non-first row and verifies its name, details, and
  actions/usage are the selected row's data.
- A refresh preserving the same identity preserves selection.
- A missing prior identity clears or selects according to one documented rule.

### A2. Make Constraints/DRC input validation safe and friendly

Current defect:

- `ConstraintsDrcView.xaml.cs` calls `BuildScalar` from an `async void` click
  handler.
- `ConstraintsDrcViewModel.BuildScalar` throws for blank, malformed numeric, or
  malformed Boolean input.
- The click path does not catch that validation exception.

Required implementation:

- Put typed validation state in the view model.
- Show a concise field-level message.
- Disable Prepare/Apply until the value is valid for the selected scalar kind
  and unit.
- Preserve Engine preparation/execution/recovery gates.
- Do not add a global exception swallow.

Required proof:

- Blank, malformed numeric, malformed Boolean, valid numeric, valid Boolean,
  symbol, and text cases are exercised.
- Invalid input does not call Engine preparation and does not escape the UI
  dispatcher path.

### A3. Keep the complete corridor result reachable

Current defect:

- `EngineDpViaCorridorService.cs` truncates `scan.Findings` to
  `DpViaCorridorResult.MaximumFindings` (200).
- `DpViaCorridorWorkspaceViewModel.RefreshFindings` searches, filters, derives
  layers, and paginates only that truncated set.
- Totals/reports can therefore disagree with the set the user can inspect.

Required implementation:

- Retain the complete typed in-memory findings collection.
- Apply filter/search before UI paging.
- Virtualize or page the filtered result so the UI does not create every row at
  once.
- Keep totals, result counts, filters, report export, selected finding, and
  navigation identity coherent.
- Retire the unused legacy JSON parsing/transport limit only after confirming
  there are no supported compatibility callers.

Required proof:

- Use more than 200 findings with a unique target after index 200.
- Verify search, risk/layer filtering, paging, selection, and exported totals can
  reach that target.
- Verify a small-result negative control remains unchanged.

### A4. Keep run failures visible

Current defect:

- Corridor `StatusTitle`/`StatusDetail` are inside the normally closed Setup
  popup in `DpViaCorridorView.xaml`.
- A toolbar Run failure can therefore leave the explanation hidden.

Required implementation:

- Add a compact, always-visible operation/result status near the Run action.
- Keep long timing/resource diagnostics in an expandable detail surface.
- Do not restore the old permanently expanded Setup panel.
- Maintain keyboard and automation accessibility.

Required proof:

- A simulated acquisition failure is visible with Setup closed.
- Success, cancellation, incomplete coverage, and failure are visually and
  semantically distinct.

## Workstream B: make architecture claims true

### B1. Reconcile the lower-SDK architecture gate

`python3 scripts/check-engine-boundary.py` currently fails on six files under
`tests/PD.NativeCampaign/Probe` and its direct
`CircuitHub.AllegroBridge.Sdk` reference.

Determine whether those probes are Bridge platform diagnostics or ordinary PD
consumer tests. Preferred outcome: move genuine platform probes to the Bridge
owner. If repository movement is outside the authorized workspace, isolate them
under an explicitly named advanced/platform diagnostic boundary and make the
gate/documentation precise. Do not silently add a broad allowlist and do not
claim zero lower-SDK seams while tracked exceptions remain unexplained.

The production projects `PD.Simple` and `PD.PcbTools` must remain Engine-first.

Required proof:

- `python3 scripts/check-engine-boundary.py` passes under its documented scope.
- `PD.EngineBoundaryChecks` still verifies emitted production dependencies.
- A representative newly introduced forbidden production reference is rejected
  by the gate.

### B2. Replace implementation-shaped conformance checks

`tests/PD.ToolsConformance/Program.cs` reads production source and declares
features complete from method names, string literals, and comment fragments.

Keep legitimate compiler/dependency architecture checks. Replace feature
completion source-string checks with the smallest useful public/model/WPF
behavior checks, prioritizing the catalog selection and input-validation defects
above. Do not create a large new test framework merely to preserve old source
spelling assertions.

## Workstream C: correct acquisition and product policy

### C1. Fence catalog publication and avoid unnecessary full-board reads

`MainWindow.xaml.cs` currently requests `SceneQuery.CompleteBoard` independently
for Padstacks and Physical Symbols, then publishes `capture.Scene` without a
`capture.RequireCurrent()`/document-generation fence comparable to the corridor
path.

Required implementation:

- Identify the smallest Engine family query that actually supplies each catalog.
- Do not guess: verify dependencies against Preview.104 contracts/tests.
- Retain capture identity/currentness through publication.
- Reject a result completed after a board/session switch.
- Share a coherent capture only where Engine's ownership and lifetime contract
  permits it; do not introduce a second cache with ambiguous freshness.

Required proof:

- A delayed read followed by a simulated document switch cannot publish the old
  catalog under the new connection.
- The selected data-family query supplies every fact each page uses.
- A changed board identifier and a negative control prevent board-specific logic.

### C2. Add declared-pair discovery as an explicit mode

`PD.PcbTools/CorridorAnalyzer.cs` currently uses `_P`/`_N` suffix matching even
though Preview.104 exposes declared pairs/Xnet sides.

Required implementation:

- Preserve the current suffix behavior as a named compatibility policy.
- Add declared-pair discovery as a separate explicit policy.
- Do not label generic Side A/B as positive/negative without authoritative
  polarity evidence.
- Surface ambiguous polarity or incomplete declarations as review-required,
  never as a clean result.
- Keep pair policy in `PD.PcbTools`, while typed pair acquisition remains Engine
  owned.

Required proof:

- Declared pair with names that do not end in `_P`/`_N`.
- Existing suffix-only board behavior.
- Xnet members, absent declarations, and ambiguous polarity.
- Renamed identifiers and a non-pair negative control.

## Workstream D: simplify PD application ownership

The following are coordinators with mixed responsibilities, not targets for
arbitrary line-count reduction:

- `MainWindow.xaml.cs`
- `Corridor/DpViaCorridorWorkspaceViewModel.cs`
- `Tools/ConstraintsDrc/ConstraintsDrcViewModel.cs`
- `BridgeSession.cs` and partials

Restructure only along real ownership boundaries:

- one application composition/connection owner;
- per-feature workflow services for product sequencing;
- presentation-only view models and commands;
- report/export components separate from capture/session control;
- debug-image capture separate from the corridor's primary workflow;
- one consistent application operation/busy projection while Engine remains the
  authoritative native admission layer.

Do not move companion activation/relaunch or faulted-session recovery in this
workstream. Those require a supported upstream Engine/Bridge contract and are
listed below as an explicit blocked boundary.

Refactor incrementally behind behavior checks. Do not rewrite all views or change
the visual design as a side effect.

## Workstream E: documentation for humans

Update all current entry-point documentation to Preview.104. Remove or mark
historical Preview.26/Preview.94 claims, and correct references to removed files
including `HorizontalFirstPlanner.cs` and `BridgeSession.PcbTools.cs`.

Create a short `docs/BUILDING-A-PD-TOOL.md` that shows one real minimal feature:

1. receive the shared `AllegroWorkspace`/presentation;
2. declare a bounded typed `SceneQuery`;
3. run PD policy over immutable Engine data;
4. preserve capture/document identity for live actions;
5. present validation, partial coverage, and errors without platform jargon.

Keep migration provenance and native qualification evidence in clearly labeled
advanced/historical documents rather than the primary getting-started path.
Replace end-user text such as “native result admission,” package-version
requirements, raw object IDs, and “architecture migration in progress” where it
does not help the user complete a task. Preserve important safety distinctions in
plain language.

## Explicit upstream boundary: investigate and document, do not emulate

The audit also found that PD's shipped launcher and recovery flow own reusable
platform details:

- `installer/pd_simple_loader.il.in` reads private `pdab__*` state, hard-codes
  protocol/version details, implements liveness/relaunch timers, and publishes a
  custom `pd-simple-focus.signal` mailbox.
- `BridgeActivationSignal.cs` consumes that mailbox.
- `MainWindow.RecoverWithNewWindowAsync`, `RecoveryHandoff`, and
  `ConnectionSwitchPolicy` reproduce session replacement/recovery interpretation.

Before changing these, inspect Preview.104 and the adjacent Bridge source for a
supported activation and atomic recovery/session-rebinding contract. If the
contract exists, adopt it. If it does not, produce a precise upstream Engine/
Bridge API proposal and stop at that ownership boundary. Do not create another
PD-specific protocol or weaken unresolved-operation fencing.

## Verification

Run the cheapest focused checks after each workstream, then the applicable final
suite. At minimum report actual results for:

```powershell
python scripts/check-engine-boundary.py
dotnet build src/PD.Simple/PD.Simple.csproj -c Release
dotnet run --project tests/PD.EngineBoundaryChecks -c Release
dotnet run --project tests/PD.Simple.Checks -c Release
dotnet run --project tests/PD.PcbTools.Checks -c Release
dotnet run --project tests/PD.Simple.DrawingChecks -c Release
```

Also run the dedicated catalog, Constraints/DRC, and corridor checks added or
changed by this work. A Linux compile is not native Allegro or GUI acceptance.
Keep those evidence classes distinct.

## Completion report

Return:

- observable behavior now working;
- exact files changed;
- tests and actual results;
- anything still requiring native Allegro/GUI acceptance;
- any upstream Engine/Bridge API gap that prevents removing PD-owned lifecycle
  machinery;
- the final diff reviewed for unrelated changes and board-specific logic.

