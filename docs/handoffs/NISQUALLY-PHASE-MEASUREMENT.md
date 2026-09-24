# Nisqually acquisition timing: current evidence and next experiment

Date: 2026-09-23. This is an **interim measurement report**, not a release
qualification or a claim that ordinary PD Simple Run is fast on Nisqually.

## Local Preview.116 source and package status

The Bridge acquisition source is committed at
`73648f0de1c34da2f4e6d7f39e9b8cd6201b2ac2` on
`sol/nisqually-acquisition-20260923`; the remote branch head was verified.
A separate clean worktree at that commit passed 8,665 Engine checks, 99
Engine bulk-capture checks, and 1,001 Host assertions. Those checks cover
generic batched-demand assignment, bounded multi-query sealed replay, no-pad
capture coverage, cancellation, resource limits, and corruption rejection.
They do not measure ordinary PD Simple Run.

A local-only `1.13.0-preview.116` SDK/Core/Engine/WPF bundle was built with
the existing ignored CircuitHub credential and public signing-key inputs.
The manifest reports `bundled_license_included=true`; the generated packages
and credential were not committed or uploaded. PD Simple was pinned to this
generation, and its production Release build and focused checks passed. The
PcbTools gate passed 240 checks; the PD Simple focused gate passed including
123 large-board corridor checks; the drawing gate passed after correcting the
success badge's production color. `Build.ps1` then produced the local setup
ZIP and passed its payload verifier. **The installer has not been installed or
run against Nisqually.** The build also surfaced and repaired an AOT-only
reflection-based JSON call in the pre-existing snapshot helper; that repair is
still local dirty source, not part of the Bridge acquisition commit.

The bounded Engine replay is now called by the PD corridor runner in groups of
at most 64 homogeneous all-net queries. Resource or unsupported groups can
split to the scalar fallback; corrupt, misordered, uncertain-cleanup, and
cancelled groups fail closed without publishing a partial scan. The full
4,814-batch public replay time, cold acquisition-to-first-result time, GUI
Browse latency, and current-board freshness are **not yet measured** on
Preview.116. The accepted under-ten-second first-use target remains open.

## Fixed input and profiles

- Local source-head baseline: Bridge `d23d4a18fd84567a4fbf813f35d9d10058e947da`;
  PD Simple `2c252117645c72ab98c985fac625c7981b362ac5`, plus the
  explicitly described dirty-worktree changes. The Astra performance branch
  was inspected, not merged into either local main branch.

- Protected saved board: `C:\Users\EMILEKOWALSKI\Desktop\boards\nisqly9z_050924.brd`,
  181,245,236 bytes, SHA-256
  `bfdbf8afe4de68abcc28f8aa296a538b56add3af3ef3e46177b2d841e608d137`.
  Every native probe used an owned disposable copy. The source hash still
  matched after the latest probe.
- The complete scalar corridor collector ran on a current-memory export copied
  into an isolated readonly Allegro 25.1 P001 worker. The worker input SHA-256
  was `aaf29504919f0c0a945168beb1316cd11532a86a6c8492f0145632cf09aa5158`.
  That is an exact worker-input identity, not an edit epoch for the primary.
- The corridor query requested nets, connectivity, layers, trace/via/shape
  objects, no contours or pin copper, and via-pad measurements for `_P`/`_N`
  net suffixes. It did **not** restrict aggressors to pair nets.

## Phase and resource observations

| Phase | Observed result | Evidence and limit |
| --- | --- | --- |
| Native route enumeration only | 364.2763 ms wall for 7,204 branches, 395,026 segments and 63 arcs | Filesystem markers around one SKILL pass in `nisqually-spatial-20260922T231803-c38bbe68`; one run, not a statistically stable distribution. |
| Native route walk plus bounds | 283.0264 ms wall for the same branches/segments/arcs, plus 35,567 vias; zero failed bounds reads | Subsequent warm pass in the same worker. Ordering/cache effects prevent interpreting its lower time as a bounds-read speedup. Shape-owner work is not included. |
| Metadata-only native capture | About 1 second native; 15 pages and 14,728 records | `nisqually-metadata-20260922T202008-6b0e0042`; worker startup made its total 33.5 seconds. Not copper coverage. |
| Complete scalar corridor native collector | About 138.7–139 seconds; 1,441 sealed pages, 446,881 records, 262,500,220 spool bytes | `nisqually-corridor-20260922T200014-4e321008`; this includes traversal, field extraction, compact-record formatting, page write/verified reread, and manifest work. Those subphases are **not yet separately timed**. |
| Instrumented verified page write/reread | 49,883.1537 ms summed marker intervals across 1,441 pages; median 20.514 ms, p95 46.7425 ms, maximum 548.9267 ms | `nisqually-corridor-page-profile-20260922T232822-6add56b7`. Each interval includes the verified writer and the end-marker write. Marker overhead is unmeasured; this is **not** an uninstrumented production page-I/O time. |
| Instrumented full native collector | 160.7927 seconds from collector-start to collector-result file timestamps; 197.823 seconds for worker operation including startup and verification | Same page-profile run. Its two marker writes per page changed total runtime, so subtracting it from the uninstrumented run is not a valid phase decomposition. |
| Worker startup, board open and verification overhead | Complete worker operation 175.8 seconds; native collector 139 seconds | Same corridor run. Isolating the collector protected the primary editor but did not reduce time to full result. |
| 64-record emitted-record cancellation-file polling candidate | Native invocation timestamps about 132.3 seconds versus 138.7 seconds for a different baseline run; whole worker 173.4 seconds | `nisqually-corridor-cancel-poll-64-20260922T221638-e0905375`. Single non-paired run: no established speedup or cancellation-observation latency. The production emitted-record poll remains unchanged; a separate production rejection-path poll has now been added and tested below. |
| Isolated batched-spatial native filter | 5.289 seconds from collector-start to collector-result markers; 38.201 seconds for the worker operation including cold startup. One all-net walk visited 610,474 native items and emitted 6,931 objects in 40 pages, 7,165,719 spool bytes | Exact-input run `nisqually-corridor-batched-spatial-20260923T000112-b66bd04a`. Sixteen disjoint synthetic windows derived from the board bounds, **not** the actual corridor batches. This proves producer-side filtering can save projection and serialization work when the requested union is selective; it does not predict PD corridor time-to-result. |
| Exact-input via-only native collector | 25.707 seconds from collector-start to collector-result markers; 60.500 seconds for the worker operation including cold startup. All 35,567 vias emitted in 459 pages, 82,784,350 spool bytes | `nisqually-vias-only-20260923T000601-53aedb28`. This is the current native via object projection with the corridor's `_P`/`_N` pad-measurement policy, not a cheap via skeleton. |
| Via-only with no pad measurements | 15.107 seconds from collector-start to collector-result markers; 51.641 seconds for the worker operation including cold startup. All 35,567 vias emitted in 222 pages, 40,345,859 spool bytes; native pad-query count zero | Exact-input diagnostic `nisqually-vias-no-pad-20260923T001930-7f2e4f84`. A generated unmatched suffix used the existing selective pad policy to isolate pad-query cost. This is **not** a supported public `None` mode or complete field projection. |
| Generic `ViaPadMeasurements.None` native check | 15.755 seconds between native collector file markers; 50.457 seconds for the isolated worker operation. All 35,567 vias emitted in 222 pages and 40,345,859 spool bytes; native pad-query count zero | Exact-input `nisqually-vias-none-20260923T015146-c6d45775` used the new supported `none` request and empty suffix list. All via identities, full non-pad payloads, and order match the complete reference; all emitted via payloads match the prior unmatched-suffix diagnostic exactly. The single-run timing does not establish a speedup over that diagnostic or the eventual product path. |
| Scoped output-budget control | 6,931 emitted objects under a 10,000-object output cap, despite 610,474 native visits | `nisqually-corridor-batched-spatial-20260923T002531-de620359`. The production source-key limit was separated from the emitted-object limit before this exact-input isolated-filter run. This proves the positive case; an explicit old-bound negative control remains pending. |
| Rejected-candidate cooperative cancellation | 202.6501 ms and 203.6806 ms from valid request publication to native cancelled result in two exact-input isolated runs | `nisqually-corridor-rejected-candidate-cancel-20260923T003802-de5e02f9` and `nisqually-corridor-rejected-candidate-cancel-20260923T004110-1e5e9a6a`. The second run first sent a wrong request ID for 150 ms; it was ignored. Both produced `kind=cancelled`, `cleanup_complete=true`, `partial_snapshot_published=false`, zero remaining native store files, and a responsive primary. These are point observations, not a worst-case wall-time bound; the 64-rejection polling interval bounds only completed candidate operations, not a long individual `axl*` call. |
| Selective successful run after rejection-path polling | 5.750 seconds native collector markers; 37.390 seconds whole worker; 6,931 objects, 40 pages, 7,165,719 spool bytes | `nisqually-corridor-batched-spatial-20260923T004551-6062b615`, same exact worker input and synthetic windows as the 5.289-second run above. All 6,931 identities, payloads and source order still match the complete reference; 3,789 selected objects are on non-pair-suffix nets. The single-run difference is not an unbiased estimate of polling overhead or speedup. |
| Actual PD-plan one-pass native spatial filter | About 40.987 seconds between native collector file markers versus 138.696 seconds full; 76.979 seconds for the isolated collector worker operation versus 175.813 seconds full | The worker timings include startup/open; native intervals are file-timestamp estimates, not an Allegro internal stopwatch. Same immutable input and 4,814 canonical batches deduplicated to 1,378 inclusive rectangles. Output: 84,532 copper objects, 453 pages, about 81.8 MB spool, versus 432,153 objects, 1,441 pages, 262.5 MB full. A first attempt hit the fixture's synthetic 10,000-output cap cleanly; the rerun used the plan's explicit 100,000 cap. Not the installed app path. |
| Same-session bounded native region repeat | Three identical fixed-window all-net reads of 63 objects each took 2.766, 2.678 and 2.761 seconds native wall | One readonly disposable worker. Private bytes began at 813,907,968 and were 1,071,722,496 / 1,119,797,248 / 851,447,808 after each GC/release; the third was about 37.5 MB above initial, not monotonic growth over three samples. The bounded region reported `complete=nil`, so these are reuse/resource observations, not qualified result coverage or public Browse latency. |
| Offline SDK decode, Engine conversion and PD planning | Full-page index/decode 5.383 seconds; via SDK decode/source validation 2.354 seconds; canonical Engine via mapping 2.522 seconds; PD plan 0.859 seconds | The isolated `corridor-scope-diagnostic` used retained files from the exact worker input and the real PD planner. These are separate-process local timings, not production Host/SDK transfer, complete-scene build, or scan time. |
| Offline plan-census resource work | 47.343 seconds for full object/page census; 72.230 seconds total including 13.541 seconds validating and hashing the large input files | Same isolated diagnostic. This is experiment overhead to establish scope, not a required production phase. Its full capture had 432,153 copper objects and 262.5 MB of native spool data. Separate replay process and memory observations follow. |
| Offline full-plan replay and analysis | 140.085 seconds for all 4,814 batches with selected payloads cached: batch record selection 32.488 seconds, canonical Engine scene mapping 101.332 seconds, identity check 0.334 seconds, PD `AcceptBatch` 1.217 seconds, final `Complete` 0.017 seconds | Separate direct-mapper process, not Engine sealed-store `ReplayAsync`: it bypasses per-batch index authentication and page I/O. It produced 1,038 findings (1,008 CRITICAL, 30 LOW), 4,763 review warnings and zero blocking coverage warnings. Peak process working set 1.463 GB, not attributable to native Allegro or the public app. |
| Generic Engine.Core batched demand prototype on exact plan | Region tree build 0.126 seconds, descriptor page decode 4.806 seconds, assignment 0.657 seconds spatial or 0.653 seconds layer-aware; peak process working set 168 MB | All 432,153 descriptors and 4,814 canonical regions, default 8,192-region budget. Zero per-batch mismatches against independent strict-decimal census: 84,531 spatial union/517,868 memberships; 45,098 layer-aware union/79,123 memberships; 8,089 unknown-layer selected objects retained. Data-only assignment, not native or sealed-store acquisition. |
| Omitted-product worker launch | No worker response or window within the fixed 90-second deadline | `default-product-20260922T234045-ca3e2214`; owned process stopped, protected small board unchanged. This is an unresolved launch-contract failure, not proof of a licensing cause. Earlier worker successes used an explicit `Allegro_performance` diagnostic profile, which is not a universal production default. |
| SDK transfer, Engine conversion/index, corridor replay/analysis/report | **Not measured on the current full Nisqually Run path** | Direct worker collector evidence cannot be substituted for public Host/SDK/Engine/PD timing. Existing Engine and corridor timing contracts can record these after integration. |
| Result Browse versus explicit Revalidate | Browse's managed path now uses an Engine ticket and metadata-only WPF source; strict Revalidate retains fresh region read. **Native GUI latency not measured.** | Focused managed checks passed, but not a timed Nisqually GUI acceptance run. |

The old SKILL `cputime` values of 20,000–22,000 for route walking and 32,000
for full-board `CLINESEGS` selection were labeled milliseconds in the fixture.
Independent wall markers show that the route walk took under a second; the
SKILL clock conversion is uncalibrated and those older labels must not drive an
architecture decision. Do not subtract its values from the 139-second wall
collector time to invent an extraction or I/O phase.

The sealed scalar capture's route census was 394,963 straight segments plus
63 arcs, matching the independent walk's 395,026 segments. Both counted 35,567
vias; the complete capture also contained 1,560 shapes. The walk comparison
checks only these observed families and counts, not full record equivalence,
ordering, shape-owner completeness, or a second capture's geometry hash.

The page-profile source was an isolated copy of `pcb_capture.il` with exactly
two diagnostic marker calls added around the existing verified page writer;
the product resident and cancellation sites were untouched. The profiled run
sealed the same 1,441 pages and 446,881 records, and a read-only byte comparison
found **1,441 identical page files, zero different, zero missing** against the
uninstrumented corridor baseline. Thus its emitted page records, ordering and
page-level spatial evidence are exactly equivalent for these two runs. The
profiled current-memory snapshot SHA-256 was
`3e219d63663219d4ea2939390e4304db96b86ead76cd5145e0382e1323c750ce`,
different from the earlier snapshot file hash; raw checkpoint hashes cannot
serve as an edit epoch. The primary answered 917/917 UI probes during this
worker run, with a 479.9093-ms maximum sample gap. These are native fixture
results, not public Host/SDK/Engine or WPF acceptance.

The batched-spatial experiment also changed only isolated copied SKILL files,
not the product collector. It retained the direct all-net walker, lonely
branches, BOUNDARY shape owners, and the original source-key generation;
only conservative bounds admission before object projection was changed. The
full and selective workers read identical immutable snapshot bytes, SHA-256
`aaf29504919f0c0a945168beb1316cd11532a86a6c8492f0145632cf09aa5158`.
An independent page-record comparison found all 6,931 selected object source
identities in the full capture, with **6,931 equal payloads, identical order,
zero missing inclusive-region intersections, and zero extra identities**.
The selection included 3,789 objects on nets without `_P`/`_N` suffixes. It
emitted 5,849 traces, 970 vias, and 112 shapes, versus 432,153 objects in the
full capture. The primary's maximum response-sample gap was 255.7478 ms and
the protected board remained byte-unchanged. The initial exact-input runner
attempt sealed valid native pages but its harness rejected the reference
snapshot's legitimate older embedded origin; the corrected origin check then
passed in the cited run. Do not reinterpret a saved snapshot hash as a live
edit-epoch signal.

The via-only experiment used the unchanged production collector with generic
`object_kinds=[via]`. Its worker input was the same exact snapshot as the full
baseline. An independent comparison found **all 35,567 via source identities,
payloads and source order exactly equal**, no trace or shape objects, and
29,806 vias on nets without `_P`/`_N` suffixes. Thus a via-only discovery
stage is semantically safe on this input, but its existing heavy per-via
projection is still a 25.7-second native operation. About 5,761 via records
have suffix-matching net names; this is not identical to PD's declared/valid
pair subject set. The primary's maximum response-sample gap was 298.6728 ms.

The no-pad via diagnostic used the same snapshot bytes and unchanged producer.
It retained **all 35,567 source identities and their order**; an independent
comparison of every non-pad field found no payload differences. The omitted
pad fields were intentionally not compared as equivalent. The 10.6-second
native timing difference and roughly 42.4-MB spool difference against the
ordinary via-only run are single non-paired observations, not a calibrated
per-pad cost. Even with zero native pad queries, the existing via projection
took 15.1 seconds; merely disabling pad detail cannot meet the desired first
interaction time. The generated unmatched suffix is a diagnostic method, not
a product API to copy.

The later generic `None` implementation is a real SDK/Engine/Host/native
selector, not a suffix trick. Its exact-input native run retained the same
35,567 via identities, order and every emitted via payload field as the
earlier diagnostic while requesting no pad measurements. Its primary editor
answered 213/213 response probes during collection (maximum sampled gap
576.8624 ms); the protected original and disposable board were unchanged.
The owned worker accepted only the board's 17.4 compatibility **Yes** and
SPMHDB-212 **OK** dialogs, not a product-tier change. This validates the
native selector on one large input but not generic field projection of all
object families or PD's two-stage use of separate subject and aggressor
observations.

The exact-plan native producer still made 287,296 pad calls versus 345,536
in the full capture. This is not pad work leaking through rejected candidates:
source and retained-page counts show 4,489 selected pad-requested vias versus
5,399 full-capture pad-requested vias, each with exactly 64 successful
layer/role measurements (32 layers times REGULAR and ANTI). Spatial screening
retains 83.1% of this expensive via population even though it retains far
fewer total copper objects. A later subject-enrichment pass should request
specific measurement pairs by stable source identity/layer/role only after
the canonical pair/width plan establishes what is needed. Padstack-name-only
reuse or a pair-net-only aggressor filter would not establish equivalence.

The rejection-heavy cancellation fixture is intentionally **not a result-
equivalence test**: it forces every otherwise eligible candidate to be
rejected after kind admission so there is no successful copper result to
compare. It exercised the newly added product `pcb_region.il` poll on the
path that had previously skipped the emitted-record check. Native cancellation
kept the original request/session/generation classification and cleanup path;
the fixture's wrong-request negative control did not terminate the command.
The protected board SHA-256 still matched after both runs. This does not yet
prove public Host/SDK cancellation or a latency distribution under different
board operations.

## Actual PD corridor demand on the retained observation

An offline diagnostic decoded the via-only native pages with the SDK compact
decoder and canonical Engine scene mapper, then called the real
`ScalableCorridorAnalyzer.CreatePlan` with the ordinary tool defaults: zero
margin, no module filter, IncludeUnused false, suffix-compatible pair policy,
and 5,000-by-5,000-mil/100,000-object batch limits. It compared the resulting
region union with every object in the exact-input complete capture. Both
collector-input board files have the same SHA-256
`aaf29504919f0c0a945168beb1316cd11532a86a6c8492f0145632cf09aa5158`;
the via identities, payloads and relative order match the complete capture.
The via-only run's unrelated top-level fresh export file has a different hash;
the collector's `input/snapshot.brd` is the authoritative measured input.

| Actual plan/census | Observation |
| --- | --- |
| Valid pairs and corridors | 1,131 pairs; 2,233 corridors |
| Layer-qualified replay batches | 4,814; no per-batch object count exceeds 100,000 |
| Inclusive spatial union, all nets | 84,531 of 432,153 copper objects (19.6%): 76,155 lines, 8,089 vias, 287 shapes |
| Native inclusive spatial selection | 84,532 objects: one conservative extra line at a 0.01-mil coordinate boundary, zero omitted strict-decimal intersections |
| Engine-style layer-aware union | 45,098 objects (10.4%): 36,942 lines, 8,089 vias, 67 shapes |
| Repeated batch materialization demand | 517,868 spatial object admissions or 79,123 layer-aware admissions summed across batches; 2,409/689 maximum in one batch |
| Page-index workload bound | 1,119,582 summed candidate-page admissions for 4,814 separate replays; 1,409 unique pages, 229 median candidate pages per batch (range 35–543) |
| Conservative unknowns and coverage | Zero unknown bounds; 8,089 admitted via roots have no index layer. Planning has 242 review warnings and zero blocking warnings, but this does not authorize a live Clear/Pass. |

These are exact **scope counts**, not native speed or final finding equivalence.
They support one indexed, batched all-net producer walk instead of 4,814
independent traversals. They also warn that thousands of small Engine replays
could dominate after acquisition; candidate-page reads and analysis timing
must be measured before choosing the next optimization. Native filtering must
conservatively retain shape owners, arc/chord candidates and unknown evidence;
the offline layer-aware number is not permission to exclude vias whose native
layer applicability is uncertain.

The one-object difference is source `src/segment/123166` (full capture
ordinal 165190): its compact minimum Y is represented as
15569.790000000001 mil, whereas five batch boxes end at 15569.790 mil.
Strict decimal comparison excludes it by 1e-12 mil; the native/float
predicate conservatively admits that conductor. This is below the 0.01-mil
native coordinate precision. The exact-plan native comparator found equal
source identities, payloads and relative order for every selected object,
with no missed strict-decimal intersection. **All 84,532 emitted record
ordinals differ** from the full capture, so complete numeric witness-index
and final finding equivalence are not yet established.

An offline counterfactual added that one native-extra line to its relevant
ETCH/S06 batch while retaining the full-capture source ordinal. It produced
the same 1,038 ordered findings and all 4,763 warning texts as the strict-
decimal run. This establishes that the conservative extra line has no finding
impact on this exact observation; it does **not** repair the selective wire
format's compacted ordinals or prove a general result-equivalence contract.

The page count above is an **upper bound from retained page bounding boxes**,
not observed Engine reads. It includes four static net/layer pages per batch;
the Engine's root index can reject objects within candidate pages. It is
nonetheless large enough that copying the native speedup into the existing
4,814-replay application path would be an unproven end-to-end improvement.
Source inspection identifies additional repeated work: each current
`EngineBulkCapture.ReplayAsync` calls `ReadSelectedRecordsAsync`, which opens,
hashes, deserializes and validates the entire sealed spatial-root index before
selecting pages. That happens for every batch, not once per scan. This is a
source-backed scaling concern, **not** an independently timed fraction of the
Nisqually run. A bounded compiled-index or multi-query replay experiment must
retain authentication, per-query coverage and resource limits before it can
replace this path.

The bounded PD aggressor replay also requested all 6,842 net catalog objects
on every batch, about 32.94 million redundant catalog projections by demand
count. The pair planner already owns that catalog, while the checker reads
aggressor names from each copper object. `CorridorReplayBatch.CreateQuery` now
requests Layers and Copper but not Nets for bounded aggressor replays; its
planning query remains unchanged and copper still includes **all nets**. The
small-fixture scalable/legacy findings comparison and the focused PcbTools
gate passed (240 checks). A controlled offline full-plan A/B also returned
the same 1,038 ordered finding rows/source identities, the same 4,763 warning
texts and zero blocking warnings with or without per-batch Nets. In that
run, batch Engine mapping took 15.305 seconds with net/layer records versus
3.620 seconds with layers only; total replay timings were 61.67 versus
57.39 seconds, with substantial variation in other phases. This is an
observed offline mapper difference and exact output comparison, **not** a
qualified public Run speedup or a statistical timing distribution. An earlier
full-net run took 101.332 seconds in mapping, underscoring runtime/GC
variability.

Filtered native output compacts emitted record ordinals. The Engine currently
uses that ordinal as `CorridorSourceIdentity.CaptureOrdinal`; equal source
identities, payloads and relative order alone cannot prove equal numeric
witness indexes or warning text. A production selective contract must carry
the original traversal ordinal (or prove an explicit identity-preserving
normalization) before asserting complete finding equivalence.

## Decision and smallest next step

The pushed Astra experiment branches were useful but are not a qualified
production fast path. Their benchmark/campaign scripts establish fixed-input
and artifact checks, a native walk-only diagnostic, polling-variant generation,
a generic managed acquisition-plan idea, and a saved-checkpoint harness. Here,
the walk-only question was answered natively, the managed planner was adapted
and compiled against the current Engine Core, and cooperative rejected-
candidate cancellation was exercised. The prior benchmark adapter measures
the scalar `CorridorAnalyzer`, not this 4,814-batch production workflow, so
its existence does not establish Nisqually Browse or Run latency. The branches
were not merged; their unqualified native variants and old PD baseline should
not silently replace the current resident or application.

The new wall evidence rules out a 20-second raw route walk in this fixture.
Keep the complete sealed collector as the equivalence baseline. The page
profile shows a material storage/verification cost but cannot assign an
unbiased fraction of the 139-second production run because marker overhead
changed the workload. Record wall and process CPU separately; do not sum
nested spans or infer an IPC residual from a CPU/wall subtraction. The
exact-input actual-plan filter establishes a shorter isolated worker operation
on this immutable input: 175.813 to 76.979 seconds in the compared fixtures.
That is not a public Run improvement: even a separate cached-payload replay
spent 140.085 seconds across 4,814 batches, and it skipped the current
Engine store's per-batch index authentication and page I/O. The first
production-oriented optimization is to eliminate repeated batch metadata
projection and add a generic bounded multi-query Engine replay that scans an
authenticated root index once per coherent operation. Its actual benefit,
coverage and memory limits require measured qualification. Native field
extraction versus verified page I/O remains insufficiently isolated; the
page-marker profile cannot supply an unbiased subtraction.

The via-only measurement changes the planned staging: do not assume that
capturing all via details is a sub-ten-second prerequisite. A generic
field-projected discovery pass should first acquire only the metadata needed
to establish via identity, net, bounds/position, and pair correlation; PD can
then request heavy via details for the actual subject identities while the
batched all-net aggressor pass remains separate. This is a design hypothesis,
not a measured speedup, and native source-key coherence and coverage must be
proved before combining those stages.

Query-directed acquisition remains the promising product shape: discover
subjects, then perform one batched spatial pass over **all-net** aggressors,
including unknown spatial evidence. Preserve the direct all-net traversal;
native box selection is not required for the demonstrated filtering gain and
cannot replace the exhaustive pass as tested: it missed five shape-owner
candidates and one policy-relevant arc-chord candidate. Before product use,
the request must carry generic regions and field scope through Engine/SDK/Host,
retain the now-tested rejection-path cancellation poll and independent
visited/source-key budget, and prove that a scoped seal cannot be replayed as
full-board coverage. The Host must also obtain a supported product launch
profile bound to the verified primary installation. Snapshot results remain
capture-time evidence, never a live Clear/Pass without separate verification.

The saved-checkpoint/extracta comparison remains separate and unexecuted.
The immutable worker input has a verified file SHA-256, but that identity does
not establish an approved saved-state policy for unsaved edits in the primary
Allegro session or an equivalent extracta data contract. No extracta timing or
result-equivalence claim is made from the synthetic checkpoint harness.
Likewise, the three native same-session bounded reads do not measure visible
Browse, explicit live Revalidate, or A→B→C latest-selection behavior in PD
Simple; those remain distinct acceptance runs.

Small next implementation/qualification sequence:

1. Qualify a bounded, generic Engine multi-query replay against scalar replay
   on the same sealed capture: one authenticated index pass per group, shared
   page reads, exact per-query payload/coverage/order, cancellation, corruption
   and resource failure with no partial result. Adopt it in PD's corridor
   runner with a scalar fallback, then measure actual 4,814-batch replay time
   and peak memory.
2. Carry generic field/spatial demands to SDK, Host and the native producer;
   keep one all-net indexed walk. Add a projection-invariant traversal ordinal
   to the versioned capture contract before replacing full capture, and carry
   requested/supplied/missing-field and spatial-scope coverage through the
   Engine. Compare final finding rows, warning texts and witness indexes on
   the same immutable input; retain the exhaustive collector as fallback.
3. Measure the installed application separately: first visible snapshot
   response, complete Run, repeated Browse clicks, A→B→C cancellation,
   process memory after disposal, and fresh Revalidate. Show snapshot capture
   time and withhold live Clear/Pass until verified. The accepted interaction
   target is under 10 seconds for first useful response, not an unmeasured
   claim that the full scan finishes in that interval.

## Addendum 2026-09-23: installed Preview.116 native campaign (attempt2)

This supersedes the "installer has not been installed or run against
Nisqually" statement above. Preview.116 was installed on a disposable board
copy and run through the ordinary DP Via Corridor / Run entry point. All
evidence below is from
`artifacts/native/p116-nisq2-20260923T030000Z-attempt2/` unless noted.
The original protected board SHA-256 still matched
`bfdbf8afe4de68abcc28f8aa296a538b56add3af3ef3e46177b2d841e608d137`
before and after the campaign.

Observed results (campaign file `preview116-nisqually2-campaign.json`,
`preview116-nisqually2-all-acquisition-receipts.json`):

- Installed loader verified (Preview.116; engine
  `1.13.0-preview.116+73648f0de1c34da2f4e6d7f39e9b8cd6201b2ac2`).
  Hidden startup and same-process reveal passed; a controlled ordinary
  acquisition cancellation cleaned up confirmed with no partial result
  published (`preview116-nisqually2-cancel-receipts.json`, both receipts
  `cleanupComplete=true`, `cleanupDisposition=confirmed`).
- Full ordinary Run capture: 193,823 ms (native command 162,739;
  transfer/seal 18,707; native release 1,266; Engine import/index 11,102).
  Full replay: 491,818 ms (256 planning replays 294,511 ms; 76 grouped
  replays over 4,814 batches 194,943 ms with zero group fallbacks;
  planning 508 ms; analysis 365 ms).
- Counts match the offline baseline: 432,153 copper objects, 446,881
  records, 1,441 pages, 1,131 pairs, 2,233 corridors, 4,814 batches,
  1,038 findings, 4,763 review warnings
  (`preview116-nisqually2-corridor.rpt`). Input status is PARTIAL
  (`HasCompleteInputs=false`) from conservative backdrill warnings; no
  live Clear/Pass.
- Browse via `DpvToolbarZoomButton`: crossing-1 6,445 ms; shape
  crossing-1011 4,610 ms; sequential A-B-C 5,299/4,524/6,466 ms.
  Seconds each, not instant. Each Browse awaited its own completion;
  rapid-fire supersede/cancel was not exercised.
- Embedded board-document toggle off/on/off: explorer entry followed the
  mode and no implicit acquisition occurred (8 receipts before and after
  each toggle; `preview116-nisqually2-embedded-modes.json`).
- Revalidate crossing-1 (attempt 3): after 600,433 ms the finding
  remained Not navigated, the image stayed disabled, and no fresh proof
  appeared (`preview116-nisqually2-revalidate-crossing1.json`). The
  campaign recorded `passed=false` (expected `DpvDetailNet` control not
  found). Exact owner processes were subsequently closed with zero owned
  campaign processes remaining. This is unresolved; do not claim live
  navigation.

Limitations: installed .116 ordinary Run fails the under-ten-second
first-use target by a large margin (about 194 s capture plus about
492 s replay). These are single-run observations, not a timing
distribution. The Revalidate failure is unresolved.

Separate direct native fixture, not public SDK/Host/PD timing or
cross-pass proof: Bridge
`_local-runs/demand-discovery-bfa26b03/native-20260923T040112-dddfb560/result.json`
reports `PASS_NATIVE_DISCOVERY_COLLECTOR` on the immutable worker input
(`aaf29504919f0c0a945168beb1316cd11532a86a6c8492f0145632cf09aa5158`):
8,144.8759 ms native collector, 46,297 ms whole worker, 35,567 vias
with same identity/order/net/bounds/xy/padstack as the v1 full run,
29,806 non-pair nets, zero pad/backdrill calls, 55 pages and 35,604
records. Cross-capture numeric ordinal equivalence is not proven and
the public managed payload/SDK path is not verified there.

Next action: the .117/public selective route is work in progress. The
new PD grouped planning change (124 focused checks, zero-warning
Windows WPF build) has not been native retimed.

## Addendum 2026-09-23: offline Engine replay-plan result (sealed-store replay only)

Separate from the installed Preview.116 native campaign above: an offline
Engine replay-plan run over the exact retained 4,814 Nisqually queries in
76 groups reports one-time plan construction 2,107 ms plus replay
14,725 ms, total 16,833 ms, versus prior offline ReplayBatchAsync
159,676 ms (report `/tmp/replay-plan-benchmark/benchmark-result.json`).
Root index selection was 507 ms versus prior 145,661 ms. The full
scene/order digest `35b759fe759f50afed85354aaee341b7bfd1bdba266e7ec5e9206279d95f060b`
matched the prior benchmark, and 40 ordinary scalar comparisons had zero
mismatches. Tradeoff: peak working set 498,905,088 bytes versus prior
178,241,536 bytes, for 432,153 indexed roots and 93,082,458 index bytes.
The current store schema carries source-ordinal metadata, so logical page
membership bytes differ from prior (7,952,606,912 vs 7,549,166,624).
This is offline sealed-store replay only: no native acquisition, Host
transfer, GUI, or PD analysis qualification. Source Bridge Engine bulk
gate 382 PASS and zero-warning Release build support the result, but the
local dirty source is not yet packaged/installed. This does not meet the
10 s first-use target; installed .116 ordinary Run remains 193,823 ms
capture + 491,818 ms replay.

## Addendum 2026-09-23: installed Preview.121 native Nisqually acceptance

Installed Preview.121 ordinary DP Via Corridor Run on a disposable
Nisqually copy completed under correlation
`5fb275a38bb2444789ee2353938567e7`: 1,131 pairs, 2,233 corridors,
4,814/4,814 batches in 76 grouped replays with zero fallbacks,
1,038 findings, 4,763 review warnings, 446,881 records, 1,458 pages,
499,732,861 stored bytes. Replay total 27,775 ms (global via replay
10,114; planning 655; bounded replay 16,054; analysis 537). Input
status is PARTIAL (`HasCompleteInputs=false`); no live Clear/Pass.
Evidence is retained in
`artifacts/native/p118-nisqually-20260923T0640Z/` as
`p121-corridor.rpt` / `.rpt.dat`, `p121-firstuse-direct.json`,
`p121-postrun-navigation.json`, `p121-memory-postrun.json`, and the
`p121-revalidated-*.png` screenshots.

First useful response while the Run was busy arrived at 7,303 ms (6
visible candidates); positive/negative net zoom took 783/750 ms with
the Run busy throughout, reporting net navigation only. This single
run meets the under-ten-second first-use target; it is not a timing
distribution.

A direct report parity review against .116 found the 1,038 finding
semantic rows in identical order and all 4,763 normalized warning
strings equal in order; 241 raw warnings differ only in source
ordinal labels. The capture carries the new
`all_board_copper_roots_v1` source provenance (468,507 roots);
witness indexes are now scene-local.

Postrun UIA navigation: Browse of crossing-1/2/3 ended historical
(crossing-1 historical before and after; crossing-2/3 unknown before,
historical after). Revalidate of crossing-1 produced a fresh bounded
witness observation (`SelectedCapturedFields`) with the review image
available, within the 10-minute budget. Embedded mode Off-On-Off
followed the explorer entry with receipts unchanged (3 before and
after) and no implicit acquisition. The UIA script wall times
(10,780/14,227/10,882 ms Browse, 151,201 ms Revalidate) include UIA
overhead and do not establish underlying navigation latency. The PD
screenshot records the captured review; the Allegro WindowDC
screenshot shows the Start Page canvas despite the board tab, so no
visual Allegro viewport verification is claimed.

Memory after the full run and navigation (single observation, not a
leak qualification): Allegro private 1,502,756,864 bytes, working set
616,865,792, peak working set 1,365,016,576; PD.Simple private
512,757,760, working set 392,798,208, peak 874,962,944.

Cancellation: the exact-input native rejected-candidate ~203 ms
cancel with the wrong-ID control stands as previously documented. A
later .121 ordinary Run attempt at 09:39 failed on the aggregate
private spool byte bound under low disk; it was not cancelled and
published no partial result. Another ordinary Run (correlation
`d851e952467249b4bc74c855396e8ef1`) captured fully and was cancelled
DURING SCAN: terminal `dp-via-corridor-run` state 2,
`cleanupComplete=true`, `cleanupDisposition=confirmed`,
`partialDataWithheld=true`, with `large-board-capture` and
`dp-via-corridor-scan` at state 0. No cancellation during native
traversal is claimed for these runs; the affected runner received
SIGTERM and produced no runner JSON. The user-visible "PCB capture
cancelled during native traversal" error text refers to an earlier
incident and is not relied on here.

The protected original SHA-256
`bfdbf8afe4de68abcc28f8aa296a538b56add3af3ef3e46177b2d841e608d137`
was unchanged before and after.

Remaining integration gap: current new source adds a visible saved
`BackgroundCaptureProduct` setting (built, tests passed) but it is
NOT installed; .121 required a hidden token. Installed acceptance
therefore does not cover the visible-setting path.

A later Windows-target cross-publish from Linux produced a local-only
`artifacts/PD-Simple-Setup-SourceSettings-20260923.zip` with the updated
setting, visible status, whitespace validation, and preference-preserving
installer. Its six manifest payload files were checked against the archive
by SHA-256; the bundled Host bytes match the Preview.121 package. The
Windows installer `-VerifyOnly` and an installed GUI run remain unexecuted
because this session rejected Windows executables before launch. The Bridge
acquisition source is staged separately from unrelated physical-symbol work,
but the same approval policy rejected `git commit`, so no new Bridge commit
or push is claimed.
