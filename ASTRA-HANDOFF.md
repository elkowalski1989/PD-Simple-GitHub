# Astra Handoff — Focused Corrective Pass (RC 1.13.0-preview.86)

Date: 2026-09-19. This pass answers the audit of `b4bc2e1` / `05b5a9d`
(Preview.85). It is a targeted correctness-and-latency pass, not another
architectural expansion, and not a claim that the navigation delay is fixed.

## Commits under review

| Repo | Commit | Content |
|---|---|---|
| allegro-bridge | `fee3794` | Corrective pass: retry affinity, surface cause, arc record qualification, region phase timing, witness contract, recovery adoption (14 files) |
| PD-Simple-GitHub | `ee497c2` | PD migration: receipt lifecycle, retry dispatch, timing surfacing, recovery carry, .86 pins + vendored packages |
| PD-Simple-GitHub | (this file + RC) | Corrected handoff + RC 1.13.0-preview.86 record |

## What changed and why

No architecture rewrites. Ownership from the prior pass is retained; the
audit's specific defects are fixed:

1. **Retry callback affinity** — `Engine/Live/EngineNativeRetry.cs`:
   explicit `dispatch` hook for thread-affine work, pre-dispatch
   cancellation/currency fencing after every backoff delay,
   `maximumAttempts` renamed to `maximumRetries` with matching semantics.
   `EngineWpfPresentation.InvokeOnDispatcherAsync` is the WPF hook; PD
   marshals capture retries through it and keeps navigation retries
   off-Dispatcher. Tested busy-then-success on a real Dispatcher with a
   sensitivity control (EngineWpfGate 190).
2. **Unavailable-surface cause** — `Engine/Scenes/SurfaceUnavailableEvidence.cs`:
   geometry role travels separately from cause (`cause=stale_fill` only
   for shape resolved copper with out-of-date fill). `UnpouredLayer`
   recommends repour only then. Verified live: crossing-3 fenced on
   causeless S12 copper with no repour advice.
3. **Degraded arc records** — `Engine/Scenes/AllegroDesignSource.Physical.cs`
   (correct file; the .85 handoff misnamed it): fallback chords are
   `UnsupportedRepresentation` with the unqualified-arc diagnostic on the
   record itself, so standalone qualified analysis rejects them. Qualified
   arcs keep `Current` analytic records. Tested on the extracted record.
4. **Drawing-first lifecycle** — PD `DpViaCorridorPublicationTracker` (new):
   the published epoch is set only from a visible receipt for the
   still-current epoch/sequence; review capture no longer republishes
   unchanged drawings, and capture failure keeps verified drawings.
   Covered by new `PublicationTrackerChecks`.
5. **Witness contract** — raw native-index overload marked `[Obsolete]`
   (compatibility path isolated, ordinary PD path uses opaque handles);
   handles report `SelectedCapturedFields` scope with a stated
   acquired-field comparison policy; final native scalar verification
   documented as kind/net/layer/bbox first-match; containing viewports
   classify `InScope`. Negative tests: ambiguity, same-bounds diagonals,
   changed holes, reordered canonical data, out-of-range ordinals.
6. **Region-pipeline timing** — managed native/decode/convert splits plus
   native `native_timing` phases (enumeration, metadata, pad, contour,
   serialization) decoded through SDK/Engine/PD into the finding detail.
   Wire names pinned by test (UnmappedMemberHandling.Disallow).
7. **Recovery evidence** — `AdoptUnresolvedOperations` carries operation
   ids, evidence, and obligations from a faulted session into the new
   Engine journal; PD reconnect adopts instead of counting. No terminal
   knowledge is invented.

## Corrections to the .85 handoff and RC

| .85 statement | Correction |
|---|---|
| "Selection-path performance: 0 ms live" | Witness matching 0–25 ms; navigation 4–12 s (see distributions). |
| "97.5% is native acquisition" | Region-acquisition pipeline; managed split now measured (decode+convert 3–93 ms). |
| "Every curve carries qualification and provenance" | Fallback chords are now explicitly unqualified records; qualified arcs unchanged. |
| "No native indices cross the boundary" | Ordinary PD path uses opaque handles; the public compat overload remains, now `[Obsolete]`. |
| Curve file `EngineWpfPresentation.cs` | Qualification lives in `AllegroDesignSource.Physical.cs`. |
| "All six phases verified live" | Live positive workflow + fence evidence; busy contention, mid-retry reselection, multi-client contention, board mutation, and recording acceptance are synthetic or unexecuted (listed in RC). |
| Recording | No recorder was running during live verification (both RCs). |

## Live performance: distributions, not one timing

Single active PD client, `ingram9z_040324.brd` (hash unchanged all runs),
Allegro 25.1. Analysis stable at 17/35/25 across 4 runs. 9 successful
selections + 1 live fence. Full table in the RC record; summary:

- Navigate: 4161–11749 ms. Region pipeline: 3671–11187 ms.
- Managed decode+convert: 3–93 ms (measured, ms resolution). Managed
  witness match: 0–25 ms. Managed total ~1% — now proven, not assumed.
- Native enumeration CPU: 4000–8000 ms, the dominant attributed cost in
  every run. Metadata/pad/contour/serialization each ≤ ~1 s.
- Residual (envelope sprintf + IPC + quantum error): 0–5000 ms by
  subtraction, bounded, not attributed to any single cause.
- Cold A 8027 ms vs warm A 7576–11749 ms: no warm-cache benefit; the
  cost is structural (full region walk per click), not cold start.
- Same-finding latency trends up within one Allegro session
  (7.6 → 11.7 s); mechanism open, recorded as follow-up.

Native clock limit (characterized live): SKILL `cputime` quantizes to
1 s and counts process CPU across threads, so per-phase error bars are
~±1 s and a phase may exceed wall (observed: enum 5000, wall 3758).
Sub-second phases read 0 unless they cross a quantum boundary.

**The 4–12 s navigation delay is not fixed.** Do not claim the
responsiveness problem is solved.

## Design: narrower verified navigation operation (not implemented)

The measurement justifies scoping the work to the click, not speeding
the walk. Two explicit modes are proposed for a follow-up; immediate
captured-result browsing is preserved in both:

- **Historical-location navigation** (fast path): zoom to the captured
  finding bounds and project the retained captured drawing without a
  fresh region acquisition. Guarantee: "a corresponding scalar native
  witness (kind, net, layer, bbox) existed at navigation time," verified
  by the existing witness-zoom resolver. No claim about fresh geometry.
  Staleness is surfaced (capture id + age in the detail), and any
  structural staleness signal (board generation change, witness
  resolver miss, ambiguity) escalates to the strict mode.
- **Strict revalidation** (current behavior, kept): full contour-rich
  region acquisition, selected-field witness match, fresh review
  capture. Guarantee: "selected captured fields matched uniquely in a
  fresh observation." Used for first visit, after edits, after
  generation change, and on demand (explicit revalidate action).

Both modes reuse the existing receipt lifecycle and viewport
classification; the mode and its guarantee travel on the navigation
result so PD can render them honestly. No new native collector is
required for the fast path; the strict path keeps today's acquisition.

## Verification evidence

| Check | Result | Log |
|---|---|---|
| EngineGate | 475 green | live86/gates/EngineGate.log |
| HostGate | 980 green | live86/gates/HostGate.log |
| EngineWpfGate (Windows) | 190 green | live86/gates/EngineWpfGate.log |
| SecurityGate | 507 green | live86/gates/SecurityGate.log |
| EngineQueryChecks | pass | live86/gates/QueryChecks.log |
| PD PcbTools.Checks | 123 green | live86/gates/PcbTools.log |
| PD DrawingChecks | pass | live86/gates/DrawingChecks.log |
| SKILL validator | pass | live86/gates/skill-validator.log |
| Live GUI (.86, single client) | 9 selections + 1 fence + cold/warm, board untouched | live86/MEASUREMENTS.md, probes, screenshot |

Log/probe/screenshot root (out of repo):
`Desktop/boards/_local-runs/live86/`; screenshot under
`Pictures/PD Simple/Screenshots/*20260918_233034_361.png` (.86).
RC record `_local-runs/rc-1.13.0-preview.86.json` carries package, Host,
resident, loaded-binary (now including Core + PD assemblies), board, and
attestation linking the built worktree diff to the final commits.

## Known failures and unexecuted work (not passes)

- First .86 host failed every region read (`getTimer` undefined in
  Allegro SKILL). Fixed to guarded `cputime`; that host discarded,
  never committed. Recorded in RC.
- Crossing-3 navigation fenced live (incomplete region). Expected
  behavior; recorded as fence evidence.
- Live busy contention, mid-retry reselection, deliberate multi-client
  contention, in-session board mutation, overlay-under-recording:
  unexecuted (retry/currency covered synthetically).
- Session-time latency growth mechanism: open.
- Narrower navigation operation: designed above, not implemented.

## Boundary contract (post-change)

- Engine owns: witness matching + scope reporting, native diagnostics
  taxonomy, retry coordination, draw/publish lifecycle + receipts,
  curve qualification + provenance, region timing, recovery journal.
- PD owns: corridor interpretation, overlay presentation, check
  orchestration, publication-epoch retention, chooser/window behavior.
- Crossing the boundary: Engine handles + typed exceptions + receipts +
  timings. No native indices on the ordinary path; no stringly failures.

---

# Addendum — RC 1.13.0-preview.89 (2026-09-19)

Commits: allegro-bridge `ee0c748` (assignment work + resolver fix),
PD-Simple-GitHub `55b17ef` (browse/revalidate, recovery carry, capture
opt-in, .89 pins + vendored packages). This file + RC record follow.

## What .89 adds over .86

Browse/Revalidate split (PD), recovery adoption carry (both), capture
opt-in (PD), plus two native ticket-resolver fixes found live (Engine):

1. Shape witnesses false-staled: the verifier compared layers as exact
   strings, but shape boundaries report `BOUNDARY/<sub>` while tickets
   carry copper truth `ETCH/<sub>` (proven live: identical bbox, layer
   `BOUNDARY/S05` vs `ETCH/S05`). Shapes now compare by etch subclass.
2. Fixed shape then false-ambiguated (`matches 2`): the BOUNDARY walk and
   the per-layer walk return the same shape twice. Dedupe by native
   identity (`memq` on dbids, `eq` verified live). A first draft used
   `consp`, which is unbound in Allegro SKILL; `listp` verified live.

## Live results (disposable copy, hash unchanged; single client)

- 9 distinct findings browsed, 166–313 ms, no region read (vs .86 strict
  4161–11749 ms on the same findings). A-B-C-D-A loop stable, no growth.
- Strict revalidate unchanged in kind: 6472 ms (native enum CPU 5000).
- Rapid reselection: Browse input disabled mid-flight; superseded op left
  no stale state; navigate envelope absorbs contention (1533 vs browse 231).
- Changed board (document switch): Browse disabled with "The Engine
  document changed. These results are historical; run again." plus
  SessionChanged invalidation. No silent stale navigation.
- Packaged .89 proof (no hot-patches): crossing-1 browse 212 ms.

## Reconciliation (item 7)

- 8-vs-9: machine recount of the .86 RC gives 8 successful + 1 fence =
  9 events; the .86 handoff's "9 successful + 1 fence" overstated by one.
- Every run names its Allegro/Host/PD PIDs and binary hashes; session 1
  ran .88 + a hot-patch byte-identical to the committed fix, session 2
  ran packaged .89.

## Not claimed

One selection-44 anomaly (browse-class result after a Revalidate click,
strict phase unobserved) and one clean Allegro `exit` from an unknown
source after the switch-back (hashes intact, no crash evidence) are
recorded in the RC as anomalies. Busy contention, multi-client contention,
in-session mutation, and overlay-under-recording remain unexecuted, as
in .86.
