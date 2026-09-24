# Building a PD tool

Checklane knowledge for PD Simple tool work: the contracts every tool
follows, where each contract is enforced, and what evidence a change
needs. New tools copy these patterns; fixes restore them.

## Worked example: the padstack catalog read

One real minimal feature, end to end. Follow it when adding a tool.

1. Receive the shared workspace. `MainWindow` owns the connection and
   reads through the shared session (`_bridge.ReadEngineSceneAsync`).
   The tool view receives one capture (or none) via
   `PadstacksView.ShowScene(scene, isLiveConnected)`; it owns no
   session and starts no native operation.
2. Declare a bounded typed query.
   `CatalogPublication.PadstackCatalogQuery()` is
   `SceneQuery.CompleteBoard(includeContours: false)` narrowed to
   `RequiredPadstackFamilies` — exactly the families the page's fact
   pipeline consumes, audited into the Engine calls it makes.
3. Run PD policy over immutable Engine data.
   `PadstackTool.SummarizeDefinitions(scene)` projects typed
   `PadstackDefinitionSummary` values; `CatalogSelection.BuildRows`
   formats stable typed rows. No mutation, no second cache.
4. Preserve capture identity for publication. The requested document
   is captured before the `await`; after resuming,
   `CatalogPublication.AcceptsPublication` compares
   requested/completed/live identities and a read completed after a
   board switch is rejected, never published under the new
   connection. `capture.RequireCurrent()` proves currency.
5. Present states in plain language. Stale reads, incomplete
   coverage, and failures reach the status line as user-actionable
   sentences ("The board changed during capture; reopen Padstacks
   for the current board."), never as package versions, object IDs,
   or platform jargon.

## 1. Engine-first, always

- PD tools call Engine contracts (`CircuitHub.AllegroBridge.Engine*`).
  They never bind the lower SDK, P/Invoke, reflection past version
  attributes, or vendor executables.
- Enforcement: `python3 scripts/check-engine-boundary.py` (source and
  package scan; fails the run on any ordinary lower-SDK seam) and
  `dotnet run --project tests/PD.EngineBoundaryChecks` (emitted
  assemblies carry zero direct lower-SDK dependencies; WPF stays out
  of reusable policy).
- The one documented exception is
  `tests/PD.NativeCampaign/BridgePlatformProbe`: raw handshake/SDK
  diagnostics that probe below the Engine facade by design. Read its
  README before touching it; never import it from product, sample, or
  ordinary test code. The gate prints its exclusion on every pass.

## 2. Ownership: PD composes, Engine contracts

- PD owns page composition (views, registration ids, catalog facts for
  display, action lists with reasons and next steps, presentation
  labels) in `PD.PcbTools` / `PD.Simple`.
- Engine owns operations, capability groups, diagnostics, workflows,
  and execution. PD references Engine identities through thin
  versioned facades — never by duplicating Engine member names into
  PD switches. Example: `PhysicalSymbolOperationLabels` resolves
  operation/group labels by Engine numeric identity, so an Engine
  rename flows without PD edits; revalues fail pinned tests loudly;
  unknown identities render explicit fallbacks.
- Record each tool's split where the next mover will find it (example:
  `src/PD.PcbTools/PhysicalSymbolOwnership.md`). Move one piece at a
  time behind behavior checks; physical moves into Engine await Bridge
  workspace authorization.

## 3. Keep tool policy WPF-free

- Tool models (rows, selection, validation, exploration, publication
  fences) live in plain classes with no `System.Windows` dependency so
  they compile into `net10.0` check harnesses via `<Compile Include>`
  links and run on Linux. Views bind typed rows and delegate behavior
  to the models.
- Verification pyramid, in order: Linux behavior checks (fast, exact)
  → Windows `PD.Simple.DrawingChecks` (view-model + live-view
  behavior on an invisible window) → GUI acceptance by a human
  (rendering, feel). No layer substitutes for another: automated
  checks never claim visual acceptance.

## 4. Freshness is a gate, not a hope

- Never publish a capture without proving it is current:
  `RequireCurrent()` on live scenes, `RequireComplete()` /
  `RequireAvailable()` on scene collections, document-identity
  comparison across awaits.
- Reads that cross `await` re-validate after resuming: capture the
  requested document before the read, compare requested/completed/live
  identities before publishing (pattern: `CatalogPublication`), and
  route staleness to an explicit status, never a silent drop or a
  stale render.
- Partial or missing coverage never renders as a complete result.
  Check `Coverage` for the families the page consumed and say what is
  missing.

## 5. Scope every acquisition to audited families

- Build `SceneQuery` values containing exactly the data families the
  page's fact pipeline consumes — audited through PD tools into the
  Engine calls they make, not guessed.
- Prefer the `CompleteBoard` kind with a narrowed family set: a
  producer that honors subsets reads less, while one that ignores
  subsets still returns every fact the page uses. Prove parity with a
  scoped-vs-full fixture test.
- Lock the query shape in tests (kind, families, contours) so silent
  widening fails loudly.

## 6. Honesty in every state

- Disabled actions carry a reason naming the exact missing capability
  or API plus the next step. Never a silent disabled button.
- Partial data surfaces as review-required with detail (which pair,
  which family, which measurement), never as a clean result and never
  by inventing labels (no P/N from Side A/B, no polarity from thin
  air).
- Screening tools name their policy and limits in code (`Algorithm` /
  `Limitations`), including which discovery policy ran.
- Status is always visible near the action that caused it, with long
  diagnostics one toggle away — never buried in a closed panel.

## 7. Evidence practices

- Checks are small console harnesses (`Check`/`Require` style) with
  hand-computed expectations, never values copied from the
  implementation under test.
- Every behavior fix ships a renamed-identifier case plus a negative
  control: the fix must work under changed names and stay silent for
  genuine non-members.
- No feature check asserts on source spelling. Architecture checks
  (boundary scans) are the only text-level checks, and they guard
  dependency direction, not feature completeness.
- Run the full relevant suite before finishing: the boundary gate,
  `PD.Simple.Checks`, `PD.PcbTools.Checks`, `PD.ToolsConformance`,
  `PD.EngineBoundaryChecks`, and — on Windows —
  `PD.Simple.DrawingChecks`. A green layer you did not run is not
  evidence.
