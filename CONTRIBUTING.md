# Working on PD-Simple

## Current source candidate

The current source targets SDK/WPF **1.12.0-preview.2** and uses the standard
PCB API in both tool workflows. See [development acceptance](docs/Development-Acceptance.md).
The new C# tool module and Explorer are source-built candidates; matching native
runtime packaging and final Allegro/GUI parity still require operator verification.
The prior handoff and measured observations below are retained historical evidence,
not a claim about newly assembled binaries. The old consumer SKILL remains
source-only and is no longer automatically loaded/copied by this application.


PD-Simple is a Windows/.NET 10 example with two tools: DP via corridor review
and two-pick point-to-point routing. Keep it independent of the full Workflow
Engine and use the packaged Bridge SDK for native integration.

## Build and check

1. Use the included SDK packages and owner handoff described in `packages/README.md`.
2. Run the build and both check projects listed in the root README.
3. Use `Build.cmd` when installer and source archives are needed.

Use `.editorconfig` for maintained source. Keep coordinates, board/session
identity, native mutation, and guarded Undo in their existing owners. Do not
replace SDK projection with cached screen coordinates or identify known boards
in production code to make a test pass.

## Native verification

Use disposable board copies in a licensed Allegro session. Never use a teammate's
working board as a mutation fixture or save test changes over an original board.

- Corridor changes: check setup, findings, filters, native navigation, report,
  raw/annotated PNG export, and alignment in both the preview and live canvas.
- Drawing changes: check focus loss, partial/full coverage, resize, minimize and
  restore; graphics must clear when the SDK's native evidence is unavailable.
- Routing changes: verify width, two real picks, native feedback, Cancel, Clear
  first pick, committed-route readback, and route-specific Undo. Hidden ETCH
  layers must reject the start before mutation.
- Installer changes: use isolated install/startup paths; verify preservation of
  unrelated entries, reinstallation, payload rejection, rollback, and uninstall.

Report compilation, automated checks, and native GUI results separately. A queued
input message or successful API call does not alone prove a rendered result.

## Native ownership

Use `pd_simple` and its SDK-bound commands as the application entry points.
The old `dp_via_corridor_check` and `dp_via_corridor_restore` commands, their
native form helpers, and their palette/marker visualization are removed.
The managed checker reads the database directly; it does not use selection or
layer-visibility changes to discover candidates.

Keep the report-only entry's dynamic request state and display/selection checks,
native navigation validation, and routing recovery in their current owners.
Future extraction must compare native findings and geometry, report generation,
navigation, and failure behavior—not just compile the C# consumer. Verify removals
in a fresh Allegro process: reloading a file does not undefine old procedures.

## High-level API integration in progress

The current dependency is the matching **1.12.0-preview.1 SDK/WPF** handoff.
Reconnect now uses its candidate discovery and verified attachment API. The
chooser's native consumer checks are recorded separately below; the SDK owner's
native attachment proof is in `packages/CircuitHub.AllegroBridge.1.12.0-preview.1.HANDOFF.md`.
Both existing tool algorithms remain retained pending their separate high-level
API migration. The following 1.11.0 findings are historical evidence, not claims
that the preview lacks its newly supplied APIs.

The integration outcome is one strongly typed, package-only PCB API consumed by
both existing tools, small beginner examples, and an unrelated pick-and-measure
consumer. Preserve the complete corridor review experience and clicked-coordinate,
horizontal-first routing with cancellation, Clear start, exact readback and guarded
Undo. C# tool modules own engineering rules; the SDK owns reusable native operations,
Windows observation and the WPF companion. Keep the existing UI/native implementation
until its replacement works through the actual application.

The earlier integration referenced matching **1.11.0 SDK/WPF** packages, verified
against the hashes and source commit in their reference PDF. Earlier 1.9.0 evidence
below remains scoped to that version. Do not mix source-built assemblies, host or
resident with the packaged runtime. The SDK checkout remains read-only here.

The 1.11.0 native boundary still prevents complete corridor migration. A null-bounds
`ReadRegionAsync` request is rejected by the host's nonempty-list schema, despite
the documented drawing-extents default. An explicit -10,000..10,000-mil region on
S03/S05/S08/S10 eventually produced a successful native snapshot, but the managed
wait expired at 90 seconds. That snapshot contains only 340 objects (258 lines,
82 vias), `truncated: true`, and per-object unavailable backdrill data on 64 vias.
This board's independent census counted 1,061 vias. The raw observation is retained
in `_local-runs/corridor-cleanup/region-native-snapshot-1.11.0.tsv`; it is not a
successful managed query or complete analysis. Do not retry it to manufacture a pass.

A later read-only inspection of SDK HEAD `9650433` plus its owner's working edits
shows a producer/protocol fix for optional empty region-bound/layer lists. It is
not in the supplied package: the available SDK/WPF release hashes still match
1.11.0. Added SDK test code is not native acceptance evidence. Complete coverage,
module bounds, stable object identity, retention, layer observation and attachment
remain unchanged; consume a matching owner-provided package before testing again.

The SDK owner still needs to supply coherent complete-board coverage, module
bounds, stable cross-region identity and navigation retention suitable for the
existing workflow. The current eight-region cache and resource bounds cannot be
worked around by silently stitching independent snapshots. Router migration also
needs active/visible layer inputs and a supported observation on netless boards.
The public 1.11.0 API does not expose safe existing-window discovery/attachment.
Do not substitute window-title/PID guesses for SDK-authoritative session binding.

A subsequent attachment-focused source inspection found uncommitted SDK APIs
`AllegroDesktop.DiscoverRunningInstances`, `AttachAsync` and `BindSessionAsync`.
They require a compatible running resident with fresh native process-identity
proof; they neither start a resident nor open a board file. These APIs are absent
from the supplied 1.11.0 package. The reconnect board chooser therefore needs an
owner-delivered matching SDK/WPF package and resident before consumer integration
and native acceptance. Source inspection is not proof that attachment works.
The consumer must retain its old connection when selection or attachment fails,
prevent switching during active operations, preserve uncertain-result/recovery
evidence, and invalidate old board-specific navigation when a switch succeeds.

The consumer now rebinds an already accepted extension identity after idle catalog
changes and exposes manual Reconnect for its original launching session. Neither
path reloads commands as an automatic refresh, replays work or clears recovery/
uncertainty evidence. Queued callbacks are fenced to their originating session.
Native corridor navigation also binds its publication to the catalog generation.
After a catalog change, retained findings/reports are historical: rebind enables a
new analysis, but cannot renew the old native navigation cache. The managed result
identity and UI now carry that generation and invalidate capture/navigation together.

The 1.11.0 package-only `ReadPcb` example completed a native read of one observed
net: 13 segments, two vias and two pins, without truncation. It explicitly reported
pad geometry, backdrill, shapes and stackup polarity as unavailable. This proves
the bounded reader, not complete corridor input. In a fresh session, the final
build marked the old analysis historical after a real SDK catalog update, kept a
new run enabled, and restored native capture after that new analysis. Manual
Reconnect under the unchanged catalog retained the new capture. The shared observer
captured the positive result (24 crossings); the SDK WPF renderer presented a live
clipped outline. This does not establish every zoom/pan/DPI spatial case.

Both final-build PNG modes produced valid 862-by-406 images. The export helper
failed to verify its requested path; the actual annotated and raw outputs were
inspected at the default Documents paths and copied into the task-local final
output. Do not report that automation-path check as passing.

The package-only `PickAndMeasure` example completed on two operator-selected
objects (component and net), returned typed clicked endpoints, and reported
117.96179890116953 mils with a successful native no-edit receipt and exit code 0.
This exercises measurement, not native Clear/Cancel or routing. An earlier example
wait remained alive after its Allegro process exited; it was cleaned up only after
that native process was confirmed absent. That cleanup was not a cancellation
receipt, and connection-loss handling still needs SDK/consumer diagnosis.

The retained router also passed final-build native checks on the disposable board:
one accepted clicked position enabled Clear; Clear returned to first-object input;
Cancel produced a native cancelled receipt with no committed geometry. A separate
7-mil H-first route completed with verified post-commit readback and armed guarded
Undo. That exact route was then removed transactionally, Undo became unavailable,
and the route was visually absent. The board file was not saved. These checks prove
the retained native router, not migration to the new typed PCB edit API or every
live-overlay spatial case.

The first consumer change replaces corridor request IDs and event round trips with
`IDpViaCorridorService.AnalyzeAsync` and `NavigateAsync`. The awaited results retain
native identity, strict payload validation and report-path checks. Cancelling a
caller's wait does not cancel native work or release the retained operation. Route
terminal admission is independent of optional feedback cleanup; unknown native
outcomes remain fail-closed.

### Required SDK capabilities

- Picking: actual clicked coordinates and units, optional picked-object identity,
  applicable layer/net information, feedback, cancellation and Clear start. Object
  centers are not substitutes for clicked positions.
- Editing: typed line/path creation on a specified layer/net with width, terminal
  outcome, exact native readback and operation-specific recovery/Undo. Transaction
  and board/session ownership remain native; do not infer safety from queue admission.
- Corridor inputs: one identity-bound, complete geometry read with design units and
  resolution; ordered conductor layers and artwork polarity; physical net membership,
  module bounds and backdrill properties; net-owned **and unnetted** vias and segments;
  via spans, drill boundaries, backdrill freshness/exclusions and per-layer regular
  pad/antipad geometry; full arc geometry; and shape contours/holes, generated-fill
  relationships and freshness. Include query provenance and explicit complete,
  absent and unavailable states. A missing/truncated collection must not mean clear.
- Freshness: revalidation of the exact board/session and relevant geometry before
  analysis/navigation/editing. Cached summary observation is not a complete fresh read.
- Presentation: the package-owned Windows observer and WPF companion must support
  the existing verified viewport, clipping, lifetime and capture boundaries while
  accepting tool-owned drawing policy. Do not copy `BoardOverlayController` wholesale
  into a library or introduce another canvas authority.

Current SDK summaries cannot satisfy the corridor input contract: via summaries
lack per-layer pad/antipad outlines and full exclusion/freshness information; segment
summaries lack arc center/sweep; shape summaries lack contours/holes. Current
collection also omits unnetted vias and generated-fill children. Do not migrate the
checker by treating those omissions as empty geometry. Legacy `_P`/`_N` discovery
and greedy pairing are tool policy, not authoritative SDK differential-pair metadata.

### Engineering investigation, separate from migration

Source inspection identifies assumptions that require representative native cases
before deciding compatibility versus intentional corrections:

- The checker queries pad/antipad geometry on the first target layer and reuses it
  across other target layers. A conceptual case with 10-mil versus 20-mil antipads
  and a foreign trace 15 mils away distinguishes that assumption from per-layer
  geometry. Exercise both layer orders; this is not a measured board result.
- Shape broad-phase padding uses one native design unit. The same physical P/N pair
  at -50/+50 mils with a 10-mil half-width and an aggressor bounding box at x=70..75
  mils produces different search extents in MILS and millimeters. The subsequent
  shape test is bounding-box based. Native-unit rounding and degeneracy thresholds
  also need equivalent-physical-board comparisons.
- Arc endpoints are treated as a chord, trace widths and foreign-via pad radii do
  not participate in the narrow test, and shape holes are not represented there.
  These are accuracy questions, not permission to manufacture migration goldens.
- Analysis can report stale backdrill data that navigation rejects. Preserve the
  distinction between report availability and safe live navigation.

For route migration, native net resolution preserves an exact common name or the
one known endpoint net; conflicting names produce unassigned etch, not rejection.
The current H-first planner uses `fix(10000 * coordinate)` equality in design units
to choose one segment versus a bend at `(end.X, start.Y)`, retaining the original
clicked endpoints. This is truncation-bucket equality, not an absolute tolerance or
exact coordinate equality. Width is supplied in mils and converted by 0.0254 for
millimeter designs. Establish boundary parity before replacing this calculation.
Do not add a second unused planner while native picking and creation remain one
operation with no consumer of a C# plan.

No engineering correction is accepted solely from these analytical counterexamples.
Keep native findings/report/navigation comparisons separate from new C# geometry
tests. Retire superseded SKILL only after actual-consumer replacement proof and a
fresh-session absence check. Final acceptance uses the matching final SDK package;
source review, compilation, mocked checks, native behavior and GUI proof are distinct.

### Native observations (2026-09-10, packaged SDK 1.9.0)

The actual corridor UI exercised the awaited service, positive findings, navigation,
reports and raw/annotated captures. Missing-module failure after a successful result
now retains the exact failure message through later SDK state refreshes; the old
result is historical and its navigation/export actions are disabled. These are
current-consumer checks, not acceptance of the pending high-level SDK replacement.

A read-only pad census of the disposable `pd-simple-demo` board traversed 1,061
vias and sampled one instance for each of 155 pad-definition DBIDs. All 1,240 named
regular/antipad queries on S03/S05/S08/S10 returned circular, centered geometry;
none varied across those four positive-polarity layers. The native helpers matched
the raw bounding-box radii, without fallback-only results in those queries. The
probe reported preserved selection, visibility, viewport and palette state. This
does not cover every instance override or establish the first-layer assumption as
a general rule. A differing-layer padstack remains a required counterexample.

The same disposable board was checked in fresh sessions with native units
`("mils" 2 100)` and `("millimeters" 6 1000000)`. Six-decimal millimeters represent
the original 0.01-mil coordinate grid exactly. Conversion requested updates to 55
dynamic shapes; fill was kept enabled and Allegro completed that update. Therefore
the comparison includes native conversion and regeneration, not just display units.

- Both runs found 37 pairs and 74 corridors. With 50-mil margin and unused portions
  included, all 24 findings and every serialized engineering field matched exactly;
  only the declared source units differed after excluding output paths and generation.
- With zero margin and unused portions excluded, mils produced zero findings while
  millimeters produced one shape finding: `U1_CLK_OE_0_3_N` on `ETCH/S05`, beside
  `PE_SW_P_NVMED1_00`. Its reported distance is 62.7149968398 mils with a 16-mil
  corridor half-width. This is an observed unit-conversion discrepancy, not parity.
- The extra finding is consistent with the source's one-native-unit AABB padding
  (1 mil versus approximately 39.37 mils) and lack of a shape narrow-phase test.
  Shape regeneration prevents attributing the entire difference to padding from
  this run alone. Do not silently bless either output as the corrected gold standard.
- Both runs completed report generation and native finding navigation, and rejected
  a nonexistent module without creating report or navigator files.

Raw observations are task-local under `_local-runs/corridor-cleanup/`:
`pad-observation-16484.txt`, `unit-mils-{default,expanded}.json` and
`unit-mm-{default,expanded}.json`, with adjacent reports. They are not source fixtures
or universal expected outputs. No original board save was requested.

## Review before publishing

Inspect the staged diff and filenames. Do not commit board files, logs, credentials,
signing material, dependency caches, or generated setup/source archives. The
explicit SDK package in `packages/` is the bundled binary dependency; the adjacent
PDF is its reference documentation. Review the intended repository contents
before pushing to `github.ibm.com`.
