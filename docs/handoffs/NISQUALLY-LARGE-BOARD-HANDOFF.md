# Codex/Astra handoff: Nisqually large-board scalability

## User-confirmed interaction contract (2026-09-22)

For the first Nisqually DP-via-corridor run after opening the board, the
interactive workflow should become usable in under 10 seconds. A complete
background scan may continue beyond that interval **if navigation is instant
and the UI remains responsive**. Do not present the background scan as a
completed full-board result before its seal and validation. Show the capture
time on snapshot-derived results. Until a safe live-current verification path
has proved the editor has not changed in a way that invalidates the result,
withhold live **Clear/Pass**; label the evidence as a captured board state.
When opening an owned test board in Cadence, accept the exact compatibility
**Proceed/Yes** and relevant **OK** prompts so the run does not stall; do not
accept a product-tier change or dialogs belonging to another process.

## Astra acquisition experiments reviewed (2026-09-22)

The user supplied `Allegro_Acquisition_Performance_Experiments.zip`; its archive
integrity passed, and read-only `git ls-remote` confirmed the claimed Bridge
head `036abb0cef5f3533e69d89169db3869ec053cd7e` and PD head
`3260269344e11af1ffab521c437329077f6e3bce`. **Do not merge the branches
wholesale into these dirty worktrees.** The Bridge additions are opt-in
diagnostics and managed prototypes, not a qualified production acquisition
change. The PD branch is based on Preview.105 while this worktree pins
Preview.115; the ZIP's benchmark adapter calls `Workspace.ReadAsync` and
`CorridorAnalyzer.Analyze`, not the current `DpViaCorridorCaptureRunner` bulk
capture and scalable replay used by the ordinary Run action. It cannot be
used as the primary Nisqually Run benchmark without updating that route.

The native walk-only/walk-plus-bounds diagnostic and cancellation-polling
variants may help isolate the measured 139-second collector cost. The audited
`pcb_capture.il` Git blob still matches the variant generator's exact source
anchor; the walk diagnostic and other polling intervals remain unqualified.
Keep the 139-second sealed corridor measurement as the collector baseline. The
earlier route-walk values labeled 20–22 seconds were derived from an uncalibrated
SKILL `cputime` conversion and must not be used as elapsed times. Use the
diagnostic pieces selectively after adaptation and native
qualification; do not import the old package pin, generated packages, or
unwired memo/planner into the release candidate.

The large-board Browse path now admits an Engine navigation ticket from its
three retained capture witnesses and asks Allegro to re-resolve those witnesses
before zooming. The bounded witness scene covers the padded ticket viewport
without claiming complete copper coverage. After zoom, PD requests only
Engine metadata to bind WPF review; requesting Layers through RegionGeometry
would still invoke the native copper-region collector, so that is not the
fast route. The explicit Revalidate action retains the strict fresh-region
witness check. Windows PD Simple Release compilation passed with zero warnings;
the focused PD.PcbTools checks (239 total) and PD.Simple checks passed. These
are source/managed checks, not a native Nisqually timing result or GUI
acceptance. The installed Preview.115 candidate is unchanged.

One licensed, disposable Nisqually experiment has now run the generated
64-record polling candidate without changing the product resident. It sealed
the same 1,441 pages and 446,881 records as the baseline. File timestamps
bracketing the native invocation measured about 132.3 seconds versus 138.7
seconds for the previous baseline; one non-paired run does not establish a
stable 6.4-second speedup and the total worker operation still took 173.4
seconds. The primary answered 789/789 UI samples with a 251-ms maximum gap;
original, disposable, and exported board bytes remained unchanged. A separate
owned worker was terminated after its first page and its incomplete spool was
cleaned, while the primary survived. **That termination is not a measurement
of cancellation-file polling latency.** This variant is not qualified or
enabled in production; it cannot meet the under-10-second interaction target.
Evidence: `C:\e2studio\allegro-bridge\_local-runs\snapshot-worker-feasibility\nisqually-corridor-cancel-poll-64-20260922T221638-e0905375`.
That run actually encountered and accepted the owned board's exact 17.4
compatibility **Yes** prompt and SPMHDB-212 **OK** warning, as recorded in
`compatibility-dialogs.txt`; it did not select Product Choices or Update to Tier.

## Sol continuation: measured large-board speed gate (2026-09-22)

The first protected-Host export slice is now implemented in the Engine source
but is **not yet a callable SDK/Engine/PD Simple feature**. It packages a private
deferred SKILL command, resolves a fixed Host apphost copy mode from a one-shot
Host reservation, checks the copied bytes against a receipt, and releases only
the exact owned files. Host compilation, 12 focused copy/package/tamper
assertions, and the full 1,000-assertion Host regression gate pass. An owned
disposable small-board Allegro run passed the real
resident package-load lifecycle and a second run passed the deferred command,
fixed apphost helper, receipt, and source-file nonmutation. Evidence is in
`C:\e2studio\allegro-bridge\_local-runs\snapshot-worker-feasibility\snapshot-package-registration-20260922T213417-5bd1c9d3`
and `snapshot-package-export-20260922T214208-77b6e21f`. Those runs did not
need a compatibility prompt; the fixture is prepared to accept exact Yes/OK
board-open dialogs. They do not establish a product worker, live Undo safety,
large-board speed, or UI acceptance.

The sealed Nisqually corridor profile contains 394,963 straight trace segments,
63 arcs, 35,567 vias, and 1,560 shapes. Of those vias, 5,399 belong to 2,689
`_P`/`_N` nets. These measured counts make per-corridor whole-board traversal
untenable and explain why a simple box query alone is not a complete answer:
shape-owner and arc-chord supplements are required. A fast path needs a reused
immutable worker index or qualified selective acquisition, with a distinct
live-current witness gate. The present 139-second full collector remains a
complete fallback, not the interactive target.

The user added an explicit real-time expectation: PD Simple must stay responsive
and large-board operations must be materially faster, not merely pass higher
resource limits. On fresh disposable Nisqually copies, current-memory export
completed in about two seconds with sampled live state unchanged. A broad
all-data stress collector emitted 8,193 pages without sealing before its
20-minute deadline and was stopped. That broad profile is not the ordinary
corridor query.

The exact corridor-shaped native profile (nets/connectivity/layers,
trace/via/shape, no contours or pin copper, via-pad measurements for `_P`/`_N`
suffixes) sealed 1,441 pages and 446,881 records within current production
limits. Its spool was 262,500,220 bytes; native collection took about 139
seconds and the worker operation including startup/verification took 175.8
seconds. The primary Allegro editor answered 831/831 responsiveness samples,
with a 354-ms maximum gap. Protected original and disposable board bytes were
unchanged. Evidence is under
`C:\e2studio\allegro-bridge\_local-runs\snapshot-worker-feasibility\nisqually-corridor-20260922T200014-4e321008`.

This direct native result proves isolation and completeness for that profile,
**not** real-time performance or a public Host/SDK/Engine/PD acceptance. The
next implementation must make normal queries selective/on-demand or safely
reuse a qualified background index with reliable invalidation. Raising limits
or shipping the same 139-second capture behind a worker is insufficient.

The follow-up metadata-only capture sealed 15 pages/14,728 records in about
one second of native collection (33.5 seconds including a fresh worker startup).
A native `axlSingleSelectBox` probe was fast but **not conservative by itself**:
the tested box returned 30 shapes versus 35 bounding-box candidates in the
complete capture; a narrow box returned one arc versus two chord/bounding-box
candidates. Evidence and fixture are in Engine's ignored
`_local-runs/snapshot-worker-feasibility/` directory, with exact run IDs in
`FEASIBILITY.md`. A fast product path must independently cover shape owners and
arc-chord cases, and prove trace/via coverage, before replacing full traversal.
An isolated complete route walk counted 395,026 segments and 63 arcs. The
original `cputime` values of 20,000–22,000 and 32,000 for full-board CLINESEGS
selection were mislabeled as milliseconds; they are not calibrated wall times.
A fresh disposable Nisqually diagnostic with independent filesystem wall
markers measured **364.2763 ms** for the route walk and **283.0264 ms** for a
second walk that also read bounds on 395,026 segments and 35,567 vias. Both
walks counted 7,204 branches and 63 arcs, and no bounds read failed. The
second pass was warm and ordered after the first, so these are not paired
performance comparisons or proof that bounds reads are faster. They do show
that the route walk is not a 20-second bottleneck in this fixture; the native
field projection, record formatting, and page path need separate measurement.
Evidence: `C:\e2studio\allegro-bridge\_local-runs\snapshot-worker-feasibility\nisqually-spatial-20260922T231803-c38bbe68`.
The interim phase report is `docs/handoffs/NISQUALLY-PHASE-MEASUREMENT.md`.
A separate isolated page-profile run added only two marker calls around the
unchanged verified page writer. It sealed 1,441 pages and 446,881 records, and
all 1,441 page files were byte-identical to the uninstrumented baseline. The
marker intervals summed to 49.9 seconds. The instrumented native run took
160.8 seconds versus about 138.7 seconds in an earlier uninstrumented run;
the difference cannot be attributed solely to markers in an unpaired test.
Thus 49.9 seconds is **not** a
qualified uninstrumented page-I/O phase. The primary responded to 917/917
probes during the worker run. Evidence:
`C:\e2studio\allegro-bridge\_local-runs\snapshot-worker-feasibility\nisqually-corridor-page-profile-20260922T232822-6add56b7`.
The high-risk timer boundary has now passed in a disposable native fixture:
`axlShellPost` deferred a fixed registered export command until after the
timer returned; `axlRunBatchDBProgram` then exported the current-memory board
in about two seconds without changing sampled editor state. The isolated
metadata worker sealed 14,728 records while the primary answered 146/146 UI
probes (229-ms maximum gap). The exact passing evidence is
`C:\e2studio\allegro-bridge\_local-runs\snapshot-worker-feasibility\nisqually-metadata-deferred-20260922T205220-858894e9`.
The public Host/SDK/Engine route is still unimplemented and unverified.
Two successive exports without any intervening edit had equal file lengths
but different SHA-256 hashes (five changed 4-KB blocks). Raw snapshot bytes
cannot act as a live edit-revision token. Until a complete native change
signal or conservative live reacquisition is qualified, fast background
results must be labeled immutable-snapshot evidence, not a current-board
clear/pass. Exact evidence:
`C:\e2studio\allegro-bridge\_local-runs\snapshot-worker-feasibility\nisqually-metadata-comparison-20260922T205857-f5e666bf`.
Board-open automation must accept the exact compatibility **Yes/Proceed** and
SPMHDB-212 **OK** prompts on the disposable test process. Do not change
product tier or edit the protected source board.

## Added outcome: optional embedded Engine board document

The active goal now also includes an optional PD Simple board-document mode
modeled on the physical-symbol workflow:

- an embedded Engine/WPF board canvas is the primary in-application review
  visual when the mode is enabled;
- Allegro remains the authoritative native editor and operation process, with
  explicit activate/open, zoom, highlight, and edit actions;
- the current PD Simple/native-oriented presentation remains available when the
  mode is disabled; the choice must be clear, reversible, and persisted as an
  application preference;
- large boards must use viewport-driven bounded reads or sealed-capture replay,
  never an implicit whole-board UI materialization;
- both modes consume the same typed Engine scene, selection, publication-fence,
  and witness-revalidation contracts rather than implementing parallel board
  data paths.

The optional mode is now implemented in source. A persisted header toggle
switches the existing shared Engine Workbench between the pinned tool-oriented
presentation and the full embedded document presentation. Enabling it performs
no capture. A successful bounded sealed-capture replay can publish its typed
`DesignScene` directly into that same Workbench as read-only evidence; native
actions still require a fresh live read and witness revalidation. Focused Windows
WPF checks cover both toggle directions, persistence, one-session ownership, and
the bounded-scene handoff. Native GUI acceptance remains part of the final
candidate campaign below.

## Status and authorization boundary

Implementation and native testing against a disposable board copy were authorized
on 2026-09-21. **Preview.114 is rejected. Native acceptance is not complete.** Its
identity handshake still crashes Allegro, and its ordinary DP Via Corridor Run
action still uses the 100,000-object direct-scene path. Successful explicit-page
bulk capture on an earlier candidate does not establish universal product behavior.

The reviewed modified source has now been packaged and locally installed once as
**Preview.115, NO-GO after independent UI-starvation review; native acceptance incomplete**.
Its second native attempt passed
hidden startup, same-process reveal/reconnect, and controlled ordinary capture
cancellation. The subsequent full ordinary run ended in a Windows AppHang closure
while native pages were still progressing; no sealed capture/report completed.
The cause and actor of that closure remain unproved. Preview.114 and its evidence are preserved; do not
overwrite or rebuild either immutable candidate. The temporary local bundled
credential source was removed after the successful bundle/PD build, and its
absence and Git-ignore rule were verified. Never print or upload credentials.

Candidate history:

- `1.13.0-preview.108` is **rejected and must not be installed**. Its package
  generation succeeded, but the PD Simple loader still embedded Preview.104 and
  would reject the resident supplied by its own package. It was never installed.
- `1.13.0-preview.109` repaired the loader-version defect and passed managed
  verification, but it is superseded and must not be used for the final native
  campaign: the final run-status production fix was integrated after that setup
  payload was published. It was never installed.
- `1.13.0-preview.110` repaired the packaging chain and was installed for a native
  campaign. It is now **rejected and must not be used as the final candidate**:
  managed cancellation stopped waiting without requesting native cancellation,
  and bulk-store identity omitted the independently verified Allegro process ID.
  The latter caused a valid sealed capture to be withheld by PD Simple's exact
  publication fence as an apparent board/session change. The fence itself was
  correct and was not weakened.
- `1.13.0-preview.112` is an intentionally unbundled, managed-test-only bundle.
  It includes the cancellation repair and compiled PD Simple with zero warnings
  or errors. It must not replace the licensed installed application or be used
  for final native acceptance.
- `1.13.0-preview.113` was built and installed once. It is **rejected and must not
  be overwritten or used as the final candidate**. Its explicit capture and
  bounded replay passed, but hidden autostart launched while Allegro was still
  settling and crashed the native process; native traversal cancellation was
  misclassified as generic failure; and the corridor path treated conservative
  via-metadata warnings as blocking coverage gaps.
- `1.13.0-preview.114` is **rejected and must not be used for final acceptance**.
  It was built exactly
  once from the documented dirty Engine worktree, copied into PD Simple, pinned,
  verified, and installed. The installed loader contains Preview.114 at all three
  materialized version sites and no Preview.113 reference. The loader template
  continues to use exactly three `@SDK_VERSION@` placeholders and no literal
  preview version. Native identity-handshake startup crashed again, and the
  ordinary corridor still failed at 100,001 objects with a 100,000-object limit.
  An explicit-page cancellation after Allegro exited had confirmed cleanup;
  that receipt is not an operator-cancellation or surviving-session acceptance.
- `1.13.0-preview.115` is **packaged and installed, NO-GO after independent
  UI-starvation review, native-incomplete**. It remains an immutable development
  candidate. Its matching SDK/Core/Engine/WPF generation, package-only PD build,
  setup payload, installed files, resident scripts, and three loader-version
  sites were verified. No Allegro or PD Simple process was launched during this
  packaging/install task. It is an unsigned local development candidate, not an
  Authenticode-signed release or a native acceptance result.

## Preview.115 package/install receipt — 2026-09-22

The once-only bundle was built from Engine commit
`d37ff5a896abfa78bda767ce184c63f4d3e25ae6` with `source_modified=true` and
`bundled_license_included=true`. Its manifest retains
`native_gui_acceptance=not_run`. The forced package-only PD restore/build completed
with zero warnings and errors. The established `Build.cmd`, staged `VerifyOnly`,
and staged installer passed; all six installed payload files and the payload
manifest match the setup ZIP and unpacked stage. Protected-host bytes also match
the Engine generation, SDK archive, restored cache, and ordinary PD build output.
All three resident files match the SDK archive. The installed loader equals the
template materialized from exactly three `@SDK_VERSION@` placeholders plus the
installation root; it has three Preview.115 references and no Preview.114 or
unexpanded placeholders.

The rebuild wrapper now recognizes the established ignored local credential
source without copying the key or forwarding a duplicate key path. Its PowerShell
parser and all eight local/explicit/unbundled-mode combinations passed. Only the
existing process-local entrypoints were used; effective Windows execution policy
remains `Restricted`. Pre-build, pre-install, and final Allegro/PD Simple/Host
process inventories were empty. No process was terminated and no board was opened
or modified.

Exact paths (WSL paths correspond directly to the Windows paths shown):

- Engine generation:
  `C:\e2studio\allegro-bridge\artifacts\development\20260922T164037678Z-f51dc8878dde4e368d9df0744e1f773f`
  (`/mnt/c/e2studio/allegro-bridge/artifacts/development/20260922T164037678Z-f51dc8878dde4e368d9df0744e1f773f`).
- PD repository: `C:\Users\EMILEKOWALSKI\Desktop\boards\PD-Simple-GitHub`
  (`/mnt/c/Users/EMILEKOWALSKI/Desktop/boards/PD-Simple-GitHub`). Matching packages
  and `development-bundle.1.13.0-preview.115.json` are in `packages/`; setup/source
  archives are `artifacts/PD-Simple-Setup.zip` and `artifacts/PD-Simple-Source.zip`.
- Unpacked setup: repository-relative
  `artifacts/build-ff0b67868481496a8fcbdd944f79f887/PD-Simple`.
- Installed: `C:\Users\EMILEKOWALSKI\AppData\Local\PD-Simple`
  (`/mnt/c/Users/EMILEKOWALSKI/AppData/Local/PD-Simple`).
- Detailed non-secret receipt: `artifacts/native/preview115-package-install.json`.
  Supplementary source inventory: `artifacts/native/preview115-build-source-inventory.json`
  (324 Engine and 110 PD source/build files, including nonignored untracked inputs;
  excludes the ignored credential and generated outputs). The manifest's ordinary
  Git-diff hash alone does not cover untracked source files.

SHA-256:

| Artifact | SHA-256 |
| --- | --- |
| SDK | `0d9247b2f028f792937ca72e09b01755f34737964d8a7aa007ceec140ef18ddd` |
| Engine Core | `518422b1a6445fed3c814544670c9306e23734ef8e331d21b6dd1d92d919037a` |
| Engine | `5372ffb1f51308ba4e80e0dae76ef7768b910c9614849c6cc48dd8ea8045bdd3` |
| WPF | `f58777061f3cdc3ae248ec3ea7ab7c3813b114cd69157927212fe4413c0974ab` |
| Bundle manifest | `21a10290ed2152803182d9902c8b632b6a1cfb33fa759fb36d5b72863e6df7fb` |
| Protected host | `2debd3bd280df1af4eb51413710c77dc2eee0dc5fd03cc099fe6f7ac863bcddf` |
| Setup ZIP | `3be9cb725d86a09dceb95235469a799048c590ba10d798962c31869e8867a824` |
| Source ZIP | `628d4ab93cc9d2a47755605650dbf3088304188f1e7cc7ea40f27d20441b5886` |
| Installed/setup PD Simple executable | `7dac8dbcc86d683227be91a0a9a619e39e416c408fe82a3cfc964b1d07f87b6a` |
| Payload manifest | `833dc4c117218d85c640d22b0bf87d46f1687cc3c15ebd85093a2571a225732e` |
| Main resident | `8a929138f0db1c58b59af371b492557a2d6152740c6f250dc9bb678057aad37f` |
| Constraint observer resident | `e781aa0ff838f7e931349e37d19aff48fa9410401d4b8f69b1b1bd3c786727e2` |
| Custom extensions resident | `0a4cca7767615c875f43f7e7ebd11a86ccb298012259541abcc1b6756db55406` |
| Version-materialized loader template | `b3f84781e753108e2be80058dba4d905dab5ae90efeafd58e4154b48f576fed9` |
| Installed root-materialized loader | `0758056b11e9fbdcd24b764cc66719ad3ae806c34d3eca33b856feb7936a13ad` |
| Engine source Git-diff | `9e7236cb5c9adbf6cbf575a701b8d797625df210cfa5ed1492c6454a74be61ee` |
| Supplementary source inventory | `8c381f847e3c74bde598d02e2ea366b601a79b9b5c5a3569701bba5099f02aaf` |

Preview.114 was preserved recoverably and checked against its historical hashes:

- Setup: `artifacts/PD-Simple-Setup.zip.previous-c830b073d57547c586dee30771e82692`.
- Source: `artifacts/PD-Simple-Source.zip.previous-70e52a59025a409690e515822fbe023a`.
- Installation: `C:\Users\EMILEKOWALSKI\AppData\Local\PD-Simple.previous-55519a162d984a33b02a3359fba878f5`.
- Startup backup: `C:\Users\EMILEKOWALSKI\AppData\Roaming\SPB_Data\pcbenv\allegro.ilinit.pd-simple-backup-55519a162d984a33b02a3359fba878f5`.

The handoff update and external non-secret receipts were written after archive
creation; the immutable source ZIP is not regenerated to include these later
package/install facts. Final native acceptance is still outstanding.

## Preview.115 native attempt 1 — retained, not acceptance

The first final-candidate attempt used the new disposable copy in
`artifacts/native/preview115-nisqually-20260922T165244Z-0dc545bb`, never the original.
Original and copy were both 181,245,236 bytes, modification time
`2024-05-09T15:58:52Z`, SHA-256
`bfdbf8afe4de68abcc28f8aa296a538b56add3af3ef3e46177b2d841e608d137`.
The final recheck after this attempt retained those exact facts.

Allegro PID 38144, hidden PD Simple PID 38520, and protected Host PID 28568
started and remained responsive without the Preview.114 identity-handshake crash.
The journal directly records automatic compatibility `fillin yes` at elapsed
00:00:33. A separate SPMHDB-212 capability warning was acknowledged only with its
owned OK button; Product Choices and Update to Tier were not used. The companion
remained hidden, `snapshot_scope=control`, and its only resident request was
custom-extension activation. Acquisition diagnostics were absent and no actual
`pcb-capture-v1-*` native data existed.

The initial runner nevertheless stopped before reveal/reconnect/acquisition:
its broad filename regex incorrectly classified deployed `ce/.../pcb_capture.il`
code as capture residue. That failed receipt is preserved, not rewritten. The
harness now classifies actual native capture prefixes and all children of known
local store roots; its eight positive/negative classifier cases and PowerShell
parser checks pass. Exact finding-ID selection (including seven-row UI paging),
phase/memory receipts, and an owned-window-DC capture with foreign-occlusion guard
are also in the corrected campaign. No installed product bytes changed.

An unrelated safe-mode physical-symbol campaign then occupied Allegro PID 37476
(`asymmetric_smd.dra`, `native-case-1/staging`) and subsequent case processes.
No heavy acquisition was started while it ran. After more than five minutes of
read-only concurrency monitoring, the Nisqually session was retained idle.
Before a separately authorized lightweight reveal/reconnect check could run,
PID 38144 disappeared. Its journal records an `exit` command at 17:09:03 UTC and
normal journal end at 17:09:04 UTC, with no crash marker or matching observed
Windows application crash/hang event. This runner did not send that exit; the
external actor is unknown. The lightweight helper's first exact-PID guard failed,
so it sent no UI input. PD Simple and Host had also exited.

The early rendered-window screenshot was refused by its foreign-occlusion guard;
no image was saved and no unrelated window was captured. Thus rendered visual
acceptance is still unproved. Ordinary cancellation, >100k sealed capture,
bounded replay/report completion, finding revalidation/navigation, embedded-mode
review, and small-board smoke were not exercised. This is a harness/interference
attempt, **not a Preview.115 product failure or a native pass**.

Receipts in that attempt directory include `preview115-nisqually-preflight.json`,
`preview115-nisqually-campaign.json`, `preview115-nisqually-classifier-self-test.json`,
`preview115-nisqually-concurrency-blocked.json`,
`preview115-nisqually-external-session-exit.json`, the Allegro journal, and
`preview115-lightweight-console.log`. The first transcript's actual filename is
`-console.log`; it is preserved under the unique attempt directory.

The initial instruction was to await external-process quiescence. The user then
explicitly superseded that boundary: ignore the unrelated symbol sessions without
touching them, and proceed with a strictly owned PID/window/process tree. A second
fresh copy and evidence directory were created at
`artifacts/native/p115-nisq2-20260922T171921Z-bb636044`, prefix
`preview115-nisqually2`. That process tree inherits its own `session-temp` TEMP/TMP
directory, and capture cleanup inventories use only that root and its exact
resident bridge directory. Foreign Allegro PIDs are non-owned context, not a
blocker. The second campaign's actual outcome follows; it is not a native pass.

## Preview.115 native attempt 2 — partial passes, full run interrupted

Evidence root:
`C:\Users\EMILEKOWALSKI\Desktop\boards\PD-Simple-GitHub\artifacts\native\p115-nisq2-20260922T171921Z-bb636044`
(`/mnt/c/Users/EMILEKOWALSKI/Desktop/boards/PD-Simple-GitHub/artifacts/native/p115-nisq2-20260922T171921Z-bb636044`).
The disposable board is `nisqually-preview115-attempt2.brd`. Owned processes were
Allegro **37752**, PD Simple **16820**, and Host **34976**. Resident identity and
the launched PD command line resolve the exact private bridge directory
`session-temp/pdbridgea37752`; no process-token padding was guessed.

The startup runner first hit a stale UIA element while the application continued
to start normally. Read-only recovery established the hidden, connected, idle
same-process session without relaunch. Two later harness-only continuations
exposed an empty-array StrictMode `.Count` bug and an unfocused transient menu
lookup. Those failures remain immutable. The fixes preserve exact owned-window
UIA scoping, wrap the empty window result in `@(...)`, and focus/expand the owned
Allegro menu before invoking its enabled command. Parser checks, the empty-result
StrictMode check, and private-temp isolation tests passed. `run3` then exercised
the real ordinary workflow successfully through cancellation; no installed
product bytes were changed.

| Boundary | Actual result |
| --- | --- |
| Compatibility/revision prompt | PASS: owned Allegro journal records automatic `fillin yes` at elapsed 00:00:28. This later direct evidence resolves the recovered startup receipt's pending Yes confirmation. |
| Hidden startup, no implicit acquisition, no identity crash | PASS for the observed session: hidden PID 16820, control-only resident snapshot, no capture data or acquisition diagnostics before explicit Run. |
| AI Workflow reveal | PASS at 17:31:12–17:31:13 UTC: the same hidden PD PID became visible and connected; Allegro PID unchanged. |
| Reconnect current | PASS in 1,861 ms: same PD/Allegro PIDs and exact bridge directory. |
| Actual ordinary capture cancellation | PASS: native traversal started, cancellation completed, no new capture/store/report residue, no partial result, and the owned process pair survived. |
| Full ordinary sealed acquisition above 100k | NOT COMPLETED: native pages exceeded 100k, but the process was closed before sealing; partial pages are not acceptance. |
| Bounded planning/dense replay and typed report | NOT REACHED. |
| Exact finding selection, fresh bounded witness/navigation, shape-hole evidence | NOT REACHED. |
| Embedded mode on/off and rendered app-owned screenshots | NOT COMPLETED; the off-mode check ran before full acquisition, but both-mode rendered proof is absent. No fresh acceptance screenshots were produced. |
| Small-board native smoke | NOT RUN: conditional on the large campaign passing. |
| Protected original and disposable identity | PASS: both retain the preflight SHA-256, size, and modification time. |

Cancellation correlation **`dcf5096fa49043e9b69b686a61ca6564`** has both
`large-board-capture` and `dp-via-corridor-run` receipts with `terminalState=2`,
`partialDataWithheld=true`, `cleanupComplete=true`, and
`cleanupDisposition=confirmed`. Capture cancellation elapsed **32,062 ms**.
The exact owned native log also records cancellation during native traversal.
This is genuine user-action cancellation with a surviving session, unlike the
Preview.114 exit-driven cancellation receipt.

The full run began at **17:31:53.137 UTC**. Windows Application Hang event
**1002**, record **15402277**, at **17:33:16.2907821 UTC** identifies process
**`0x9378` = 37752**, with the matching process start time; its related report is
`AppHangB1`, report ID `8acf07e4-b052-45ba-8843-b6907b3189da`. This is not evidence
of the prior `axlCursorGet(nil)` startup access violation. All three owned
processes were gone by the post-failure inventory. This harness did not terminate
them, and no normal exit is recorded in the retained journal. The actor that
triggered closure is not established.

Native page emission was still progressing **2.038 seconds before** the hang
event: page 919 was written at **17:33:14.2524149 UTC**. Retained diagnostic residue
contains **920 unsealed pages**, **279,676 records summed from page headers**, and
**167,521,930 page bytes**. These are unauthenticated partial-capture observations,
not complete coverage or a sealed-store result. They do not establish a native
traversal deadlock. No retry, process relaunch, residue deletion, board save, or
rebuild was performed after this failure.

Late full-run receipts appeared after the harness's final snapshot. Correlation
**`3b9db9324eb0431eae8e4bfea8fd5a3b`** has failed (`terminalState=1`) capture and
whole-action terminals after **104,073/104,074 ms**, respectively, with
`partialDataWithheld=true`, `cleanupComplete=null`, and
`cleanupDisposition=unknown-before-store-publication`. Do not confuse this
unconfirmed failed-run cleanup with the earlier successful controlled cancellation.
The late-terminal supplement explicitly supersedes the initial forensic receipt's
observation that no full-run terminal had yet been seen.

Last sampled full-run private bytes were **1,356,492,800** for Allegro and
**192,618,496** for PD Simple. Samples are retained observations, not a continuous
peak-memory measurement. Original and disposable remain **181,245,236 bytes**,
mtime **2024-05-09T15:58:52Z**, SHA-256
`bfdbf8afe4de68abcc28f8aa296a538b56add3af3ef3e46177b2d841e608d137`.

Decisive files under the evidence root:

- `preview115-nisqually2-recovered-startup.json` and `preview115-nisqually2-allegro.jrl`.
- `preview115-nisqually2-run3-activation.json`, `preview115-nisqually2-run3-reconnect.json`.
- `preview115-nisqually2-run3-cancel-receipts.json`, `preview115-nisqually2-run3-memory.json`.
- `preview115-nisqually2-run3-campaign.json` and `preview115-nisqually2-run3-console.log`.
- `preview115-nisqually2-run3-failure-forensics.json`: exact matching hang event,
  original/copy recheck, journal Yes evidence, and hashes for all 920 retained pages;
  SHA-256 `ff56d7fa0046f2584b4db2efdb24e3fa8da6582a5edd3be3f9382a5bb038767c`.
- `preview115-nisqually2-run3-late-terminal-receipts.json`: all four exact-owned
  acquisition receipts and timing correction;
  SHA-256 `3f2bdf1cb9e6e2e64e3fdf66962d0c343f7751899ff54d2d2ed379822dad13e6`.

The large campaign therefore **did not pass acceptance**. Preserve the installed
immutable candidate and retained evidence; determine the closure cause or a
concretely changed observation method before another native attempt. No inference
of GUI/report/witness success is permitted from the partial records.

Subsequent read-only inventories at 17:38:23 and 17:39:59 UTC, and the final review
receipt, found no Allegro/PD/Host/PhysicalSymbolNativeChecks processes or identifiable
symbol-campaign launcher. The identified
`tests/AllegroBridge.PhysicalSymbolNativeChecks/Program.cs` contains exact staged
document fencing and no process launch/termination/close logic. Its external
launcher was not identified, so a broad-cleanup interference theory is neither
proved nor excluded. No foreign board/evidence artifacts were read. All five
temporary WER attachment paths named in the owned event were absent; the normal
user CrashDumps path yielded no PID-37752 dump. Windows denied access to some
ReportArchive content. No permissions/policy were changed, and no retained
matching WER dump/report is established beyond the preserved event XML/report ID.

`preview115-nisqually2-run3-postfailure-review.json` preserves those limitations,
the exact event 1001 XML, and successful parser checks/hashes for the five changed
campaign scripts; SHA-256
`cfc4a9999069e533c68555e612495f666365e899ffb2228a57bef560df3ffef7`.
The coordinator then classified Preview.115 **NO-GO on UI starvation following
independent review** and stopped further execution pending cooperative native
scheduling work. The actor that triggered the observed closure remains unknown.
**Attempt 3 was not launched**: no third copy, evidence directory, or process was
created. The proposed no-UIA capture-wait change was not implemented before the
stop instruction. The current harness still queries owned UIA controls during
capture waits and must not be represented as a no-UIA diagnostic runner.

## Historical package hashes

Historical Preview.114 hashes (rejected native candidate):

- SDK: `601cf19e7531b839cb6bcbef268a5d9439866cbb6dd6093071c2212c2fe86533`
- Engine Core: `360623187a8e5a19bf922739ef18a3ddbda3b6a4df5d6ab64829aec472de544d`
- Engine: `2a20feb050a3df1cb072c992c8bcb4f96236f1fbd44694a2260e6eb3f7b0ee0b`
- WPF: `8bf24503dce3e4c3b64df001514c439c75acbdd7e3194474f154f05fe498f5ee`
- manifest: `8ebde72a49f4395c630713d0e93ee4e56e2f7e45f85b4feaecf13b5dcbef11a3`
- protected host: `7947404b58f73c1742327d812d7fb06868f8aba2518090a0f7c686a4564d420c`
- setup ZIP: `a417236074b4d68b1c6520ccd16ad6be4eafd81e259aba3e918f927e21d26861`
- source ZIP: `f83f38737d523a5ebbd44ea6337f4a43a2f02ff48c275fe352a5ba635ae9e007`
- setup PD Simple executable:
  `1af46db2989866f1366806c6f6c0d946c368d7553f0ca39a8ce9ebf0ade14e20`
- Engine source-diff SHA-256 recorded by the immutable manifest:
  `257add0d4551c19f212cd264070215e541479f2049106af2f8f2ce6ca7b030b1`

Historical Preview.113 hashes (rejected native candidate):

- SDK: `b2a00adeec93172f083a631e624b4a4969ec09d636823681f638c4f331882c30`
- Engine Core: `7515e4744ece3ab33ab448d6f558b889f465f35a3f4b7af79f114a2a6fbd1e5b`
- Engine: `6b957a9080a97e00c445bea10f2d6a4a099fd73afa62d4a52426f915cb2fddf9`
- WPF: `f7e5775d9c3054afa8c369012589e9caead4dc46414421ce1d20911edc2ad6e8`
- manifest: `dcf912be10905a786d141c2fed0c691bb90489fbd3f4adb6f6da7554fc3de509`
- protected host: `694f9cc6c11760083e619b04f3d27a5335e50418910093231d2832ad34d48836`
- setup ZIP: `3e55948febfef0f6e934b3f1dc7890c7cfeb341bfc9b6916804b197695a79b9c`
- source ZIP: `72fe2618a514a07a8b47a198e2cf66fdd5e48a4b57407e8fe9e303326d9ea930`
- setup PD Simple executable:
  `bd6cb9c235407f8f26ef5e3975a1324e734d17a1d90dbad3428031f50c9c00ed`

Historical Preview.110 hashes (rejected native candidate):

- SDK: `0afe407f3406c96207c83c4c155908afd3a69df532d8a6f8d6767592d9e0dc67`
- Engine Core: `a7c06b7e43c8acca16a0e84b2a7e8c4797826ae67e2bc41818e740070a97ee9b`
- Engine: `76f664ab65f546b8481a346bddc264ffd05f575ff1365e390109d9c3c54766af`
- WPF: `f4ae30bed58b76d97edccf2b05b9909e4db686ac7083e2d61bd91abb1f42cd99`
- manifest: `5bac8ec9a05139951f8cc2b4a70b3f5a517b7f617c34025f3529bc4f404e126d`
- protected host: `614c94d352dce6f859cb171fa674443876fd5d3ae1685624bd8f7fef51613a52`
- setup ZIP: `f596cf137e87f47c15a7e6f0f537741022aaf465290ea98ab8dfb5d807f61ee4`
- source ZIP: `ccfc95928b28037cf7aa664c51d1f3688b2b8a73ba947fd1038590208610bd74`
- setup PD Simple executable:
  `48714b41b080babc03259b0f88e8fbb7913a0d7a11fafaa1e254362b4c228a1b`

Historical Preview.109 package hashes (managed candidate only; do not use for
the final native campaign):

- SDK: `9cc50dc93c60c091778a16ba377f6f00ce78b7fc13e5ed732c32e3adb95487e6`
- Engine Core: `eacfac8420e5026e065d5cb84cc30ecde216c24cf7dda409cdee79d2ac0d7774`
- Engine: `be02b3e00409344e0bd1c148cb4c129908a587ca5b29d664bf3e462bacec164f`
- WPF: `1d4fa92247b583d73f9a5579d671eb62956aad6d1d24268ca5f7bbfa89d98f75`
- manifest: `c882f51257c1ff6d9bc2ffcf1e53cfafbdc312399a455e23858f77555f376aa6`
- protected host: `ea3417643c3d2c0cd47700d759e4e4c8a2ccf44ca64fe7ed44ed74c58feff2d9`

Managed proof completed:

- Engine exposes selective, quota-bounded bulk capture and separately bounded
  replay with typed phase timing and structured resource failures.
- PD Simple provides explicit large-board capture; it does not capture implicitly
  during hidden startup or page construction.
- The scalable corridor path performs one global via-planning replay followed by
  bounded layer batches, preserves capture-scoped source identity, retains all
  typed findings, and freshly revalidates a selected finding before navigation.
- Capture-token correlation, session/board/process generation fencing, durable
  privacy-safe receipts, cancellation/incomplete-result handling, and post-report
  publication fencing are implemented.
- Selected-finding navigation now retains a distinct fresh detailed-evidence
  result. Qualified current `ResolvedCopper` regions, islands, curved holes,
  observation identity, operation correlation, witness scope, and phase timing
  remain available without changing Engine's intentional
  `SelectedCapturedFields` witness semantics. The UI states that the crossing
  remains from the original scalar scan; missing or stale contours never become
  a clear conclusion.
- Historical Preview.114 managed verification passed 990 native-host assertions,
  35 Engine bulk-capture checks, 613 Engine checks, 200 Engine Workbench
  assertions, 23 physical-symbol package assertions, 219 PD.PcbTools checks,
  34 focused large-board workflow checks, and 26 scalable-corridor checks.
  The complete PD Simple managed suite and 10 embedded board-document checks pass,
  the full WPF application compiles with zero warnings and errors, resident SKILL
  validation passes, and the Engine boundary gate passes across 118 maintained
  C# files and 17 ordinary projects.

Native Preview.110 evidence completed:

- The original and disposable board hashes were both
  `bfdbf8afe4de68abcc28f8aa296a538b56add3af3ef3e46177b2d841e608d137`
  throughout the run.
- Hidden startup launched one PD Simple process without showing a window or
  performing an implicit capture. Allegro's enabled **AI Workflow > Open AI
  Workflow** command revealed that same process.
- The explicit capture ran for 178,673 ms. Allegro private memory rose from about
  1.193 GB to about 1.827 GB while PD Simple and the protected host remained
  responsive. The old native object-catalog exception did not recur.
- The sealed native store contains 439,032 records in 1,430 pages: 432,153
  physical objects, including 395,026 traces, 35,567 vias, and 1,560 shapes,
  across 32 layers. It records 602,625 native object visits and 260,441,788 spool
  bytes with every requested coverage family available and no exhausted resource.
- PD Simple correctly withheld that candidate because its published capture
  identity lacked the verified process ID. Preview.113 source now projects the
  canonical live document identity at the Engine bulk-store owner and has both a
  positive missing-process test and a changed-board negative control.
- Preview.110 cancellation returned after 2,077 ms while the native operation
  continued to completion and retained a 251 MB resident store. Preview.113 source
  now retains the operation handle, sends native cancellation, waits for its
  terminal, requires structured cleanup proof, and never presents unknown/failed
  cleanup as ordinary successful cancellation.
- Preserved evidence is under `artifacts/native/`, including
  `preview110-capture-1-4.seal.json`, `preview110-acquisition.jsonl`, resident
  identity/snapshot/sync files, and UI screenshots.

Native Preview.113 evidence completed:

- A no-autostart control remained stable, and manual **AI Workflow** activation
  retained one Allegro/PD Simple/Host process tree.
- Explicit scale capture completed in about 191.9 seconds with 439,032 records in
  1,430 sealed pages and 479,908,062 stored bytes. Exact process identity was
  present and the publication fence accepted it.
- A bounded replay completed in about 2.4 seconds with 7,352 records from 50 pages,
  producing 6,842 nets, 32 layers, and 473 copper objects.
- The scalable corridor stopped on `via:backdrill` and
  `via:active_via_layers`. Source now classifies those known conservative
  via-metadata limitations as review warnings while unknown/insufficient coverage
  remains blocking and can never produce a clear conclusion.
- Cancellation removed the unpublished native files but returned generic failure
  with `cleanup_complete=false`. Source now sets the dynamic cancellation
  classification at every polling site so the outer capture owner publishes the
  structured cancelled terminal with cleanup/no-partial evidence.
- Hidden autostart connected and then Allegro exited with access violation
  `0xC0000005`. Source now requires ten consecutive idle-board timer ticks
  (20 seconds) after design availability before launching the hidden companion.
- The new reconnect-current harness was exercised against the retained owned
  Preview.113 disposable-board session after its modal-dialog automation was
  corrected. Reconnect completed in 1,643 ms, retained PD Simple PID 30384 and
  Allegro PID 37560, and returned to connected/idle. The next candidate must repeat this
  proof; Preview.113 evidence is not a substitute for final-candidate acceptance.
- Evidence is retained under `artifacts/native/` with Preview.113-specific names;
  it must not be replaced by Preview.114 evidence.

Native Preview.114 rejection evidence:

- `artifacts/native/preview114-allegro.jrl` identifies Allegro PID 5852. The
  companion connected at elapsed 00:00:56, followed by the native crash message
  at 00:01:01. The actual session directory is
  `%LOCALAPPDATA%\Temp\pdbridgea05852`; `resident-identity.tsv` explicitly says
  `process_id\t5852`. Constructing `pdbridgea5852` would inspect a nonexistent
  directory and cannot prove the absence of capture artifacts.
- Astra's retained-minidump/disassembly analysis, not a fresh reproduction,
  found the same fault in Preview.113 and Preview.114: read access violation at
  address `0x138`, `RSI=0`, fault `allegro.exe+0x8265cc0`. The `axlCursorGet`
  wrapper maps to RVA `0x6b6d570` and the stack return to `0x6b6d61b`. The
  initiating chain is `EngineSessionRuntime` → Allegro desktop binding →
  `VerifyResidentIdentityAsync` → V4 native-view request →
  `pdab__read_native_view_values` → `axlCursorGet(nil)`. Waiting longer at
  startup did not remove this graphical sampling from identity verification.
- Preview.114 dump:
  `artifacts/native/allegro_25.1_P001_4349869_AllegroMiniDump.dmp`, SHA-256
  `e1b1c9c99bf3884eecb0379050583bb482958a0506b634b9d7df0d3c3fa3da16`.
- The existing durable log is
  `C:\Users\EMILEKOWALSKI\AppData\Local\PD-Simple\diagnostics\acquisition.jsonl`
  (WSL: `/mnt/c/Users/EMILEKOWALSKI/AppData/Local/PD-Simple/diagnostics/acquisition.jsonl`).
  Receipt `c342ff92635340e79f6108d521f03d23`, started
  `2026-09-22T14:46:14.3775382Z`, records `caller=dp-via-corridor`,
  `userAction=Run corridor check`, `feature=engine-scene-read`, native PID 3928,
  and `scene_resource_exhausted`: 100,001 objects observed / 100,000 allowed,
  after 28,636 ms. Partial data was withheld and cleanup was confirmed.
- Receipt `c778217c50c04752b10b2e625a32ab30`, started
  `2026-09-22T14:48:10.0688728Z`, records the separate explicit
  `large-board-tools` / `Capture large board` operation, native PID 3928,
  cancelled after 130,393 ms with `terminalState=2`, `cleanupComplete=true`,
  `cleanupDisposition=confirmed`, and `partialDataWithheld=true`. Allegro exit
  caused this cancellation; it does not prove the ordinary Cancel button or
  same-process survival. Preserve these receipts; do not replace them with
  evidence from a later candidate.

Preview.114 package/install preparation, historical only:

- explicit approval was received for the local bundled-license input; the
  immutable package generation completed once, `Install.ps1 -VerifyOnly` passed,
  and the installed host/PD Simple hashes match the verified setup payload;
- the central package pin is `1.13.0-preview.114`, all post-pin managed behavior,
  WPF, conformance, Engine-boundary, and resident-SKILL gates pass, and the fresh
  disposable board copy initially matched the original board byte-for-byte;

- those package/install facts do not change the native rejection above.

Still required before calling the Nisqually incident closed:

- publish and verify a new immutable candidate with the source repairs;
- repeat hidden startup, reconnect, and AI Workflow activation, then use the same
  ordinary DP Via Corridor Run action to capture and analyze Nisqually above
  100,000 physical objects through sealed acquisition and bounded replay;
- confirm the repaired cancellation receipt says cleanup is confirmed and no
  resident capture store remains;
- freshly revalidate an ordinary selected finding and inspect current bounded
  witness/navigation evidence; retain native shape-hole evidence where available,
  and explicitly mark a bounded candidate search that did not find one;
- exercise both states of the embedded board-document toggle and fresh finding
  navigation in the enabled mode, without an implicit capture on toggling;
- recheck and retain the unchanged original-board hash, size, and modification time.

Do not modify the source board:

- Windows: `C:\Users\EMILEKOWALSKI\Desktop\boards\nisqly9z_050924.brd`
- WSL: `/mnt/c/Users/EMILEKOWALSKI/Desktop/boards/nisqly9z_050924.brd`
- Observed size: 181,245,236 bytes. File size is not an object count.

Application repository:

- Windows: `C:\Users\EMILEKOWALSKI\Desktop\boards\PD-Simple-GitHub`
- WSL: `/mnt/c/Users/EMILEKOWALSKI/Desktop/boards/PD-Simple-GitHub`

Engine source used to produce the historical Preview.110 candidate:

- `/mnt/c/e2studio/allegro-bridge`
- Manifest source commit:
  `d37ff5a896abfa78bda767ce184c63f4d3e25ae6`
- The manifest records `source_modified=true` and source-diff SHA-256
  `a5a48896d384f5b774ad778c1a5a0d6f111a6abd822e60bb6a5da9a7b5a68b2b`.

The worktrees may be dirty. Preserve existing work. Do not reset, revert, commit,
push, or copy old resident adapters into the installation. Any native validation
must use a recoverable/disposable copy or a rigorously read-only journey and must
retain the original board unchanged.

## Incident

Allegro connected PD Simple to `nisqly9z_050924.brd`, then reported:

```text
*Error* The native object catalog exceeds its resource bound.
```

Several optional-adapter warnings appeared during resident loading. Treat those
as a separate packaging/capability investigation unless evidence proves a causal
connection.

## Verified root of the reported exception

The exact throw is in:

`/mnt/c/e2studio/allegro-bridge/src/AllegroBridge.Host/Skill/Pcb/pcb_region.il`
around lines 1492-1500:

```lisp
(when (geqp count (arrayref sink 'objectLimit))
  (error "The native object catalog exceeds its resource bound."))
```

This means the streamed native capture reached a configured object-record
ceiling before emitting another candidate. It does **not** prove:

- process or system RAM exhaustion;
- board corruption;
- a serialization crash;
- a failure caused by the optional adapter warnings.

The ordinary typed Engine path supplies the limiting value:

- `SceneQuery.MaximumObjects` defaults to 100,000 in
  `src/AllegroBridge.Engine.Core/Scenes/DesignScene.cs` around line 81.
- That path validates no more than 200,000 around line 135.
- `AllegroDesignSource.AcquireAsync` forwards the query value as
  `AllegroPcbCaptureOptions.MaximumObjectRecords` for `CompleteBoard` in
  `src/AllegroBridge.Engine/Scenes/AllegroDesignSource.cs` around lines 58-75.
- `PD.PcbTools/CorridorAnalyzer.CreateSceneQuery` inherits the 100,000 default.
- Engine WPF's Capture Board action also uses default
  `SceneQuery.CompleteBoard`.
- PD Padstacks and Physical Symbols currently request `CompleteBoard` in
  `src/PD.Simple/MainWindow.xaml.cs` around lines 638 and 684.

There is a second, different ceiling after native capture. Engine's snapshot
assembler spends the same `MaximumObjects` budget across nets, modules,
components, pins, connectivity, and copper. Native emission counts physical
objects. See
`src/AllegroBridge.Engine/Scenes/AllegroDesignSource.Snapshot.cs` around lines
380-413 and 782-785.

Therefore, merely raising the native limit can move the failure into managed
scene assembly. Do not implement a limit-only fix.

## Important uncertainty: the triggering feature is not proven

The retained journal establishes:

- installed application:
  `C:\Users\EMILEKOWALSKI\AppData\Local\PD-Simple\app\PD.Simple.exe`;
- board `nisqly9z_050924.brd`;
- native PID 88556;
- connection at elapsed 01:34;
- `pd_engine` at 02:05;
- resource-bound error at 02:40.

The journal is `/mnt/c/Users/EMILEKOWALSKI/Desktop/boards/allegro.jrl` around
lines 20, 40, and 44. It does not record WPF button clicks. The closed session's
command-exchange directory is empty, so the originating query and actual limit
cannot be recovered.

Current source does not establish that hidden startup automatically performs a
complete-board capture. Do not call this a background-startup failure without a
new correlated request trace.

## Existing Engine capability to adopt first

Preview.104 already has a public typed bulk workflow in
`src/AllegroBridge.Engine/Live/AllegroWorkspaceBulkCapture.cs`:

- `AllegroWorkspace.CaptureBulkSceneAsync(EngineBulkCaptureOptions)`;
- `EngineBulkCapture.ReplayAsync(SceneQuery)`;
- `EngineBulkCapture.ExportAsync(...)`;
- `EngineBulkCapture.OpenAsync(...)`.

The relevant implementations are around lines 145, 202, 244, and 267. Defaults
are independent of a small materialized scene: two million native object records,
ten million visits, and explicit page, spool, native-snapshot, and Engine-store
byte budgets.

Use this existing API as the foundation. Do not invent a PD-specific transport,
call the SDK directly from PD, or design a replacement snapshot layer before
evaluating the current Engine bulk contract.

Known bulk-path limitations that must be measured or addressed:

1. Bulk acquisition currently requests broad metadata/pins and defaults contours
   to true. Corridor needs a narrower scalar capture.
2. `ReplayAsync(CompleteBoard)` still materializes all selected records into one
   `DesignScene`; it is not unbounded scalability.
3. Each regional replay scans/hashes the full spatial-root index, which may be
   costly for repeated small queries.
4. A regional replay selects complete spatial objects and contour descendants;
   a very large plane can dominate a small region.
5. Metadata and decoded descendants can exceed a requested root budget.
6. Module selectors are currently rejected for bulk replay.
7. Replayed scenes are historical evidence, not live mutation authority.
   Existing fresh-witness/revalidation rules remain mandatory.

## Required ownership

| Owner | Responsibility |
| --- | --- |
| Bridge resident/SDK | Bounded, authenticated, page-sealed native capture; structured resource evidence; cancellation and cleanup. |
| Allegro Engine | Bulk-store lifetime, spatial index, bounded typed replay/materialization, query planning, coverage, and public diagnostics. |
| `PD.PcbTools` | Corridor pair policy, corridor construction, candidate analysis, risk classification, and deterministic result semantics. |
| `PD.Simple` | One ordinary user workflow, progress/cancellation, internal sealed acquisition and bounded replay, and honest status/results. |

## Staged implementation history and retained requirements

The explicit large-board page in Stages 1–2 established the scale foundation.
It is now a diagnostic surface. Final acceptance requires the ordinary DP Via
Corridor Run action to apply that foundation internally, on every board size.

### Stage 1: retain the request and quota evidence

Before another native Nisqually attempt, add durable, privacy-safe acquisition
diagnostics containing:

- feature/caller and explicit user action;
- hidden/visible application state;
- application and package version plus resident/host identity;
- query kind, families, copper kinds, contour choice, region/module/layers;
- native object, visit, page, spool, snapshot, store, and materialized-scene
  budgets;
- operation/request correlation plus session and board generation;
- phase timings and terminal receipt;
- on failure, exhausted resource, configured limit, observed count if known,
  cleanup result, and confirmation that partial data was withheld.

Prefer a structured Engine resource exception/result over parsing the English
message. If the existing native error does not carry observed/limit/phase data,
extend Bridge/Engine at that owner rather than inferring values in PD.

User-facing failure must say what happened and what action is available, for
example:

> This capture reached its 100,000-object working-set budget. Use a bounded area
> or the large-board capture workflow.

Never silently retry with a larger budget and never convert failure into zero
findings.

### Stage 2: prove one bounded bulk journey

Add an explicit large-board acquisition path using
`CaptureBulkSceneAsync`. Start with contours disabled and one bounded regional
Explorer journey. Retain the sealed Engine store for the exact session/board
generation and dispose it deterministically.

Do not migrate every feature at once. First prove:

- capture progress and cancellation;
- structured resource reporting;
- one typed regional replay;
- a selected object/finding can be freshly revalidated before Allegro navigation;
- board-generation change invalidates reuse;
- no SDK reference enters PD production.

Add or extend Engine bulk options for requested families, copper kinds, and
via-pad measurement selection if Preview.104 cannot express the corridor's
selective scalar capture. Keep native capture budgets separate from replay/
materialization budgets and name both clearly.

### Stage 3: scale corridor without changing its semantics

Use a two-pass design over one sealed capture:

1. Obtain/index all global net/pair/via facts required by the selected pair
   policy.
2. Perform the existing global via pairing and construct corridor envelopes.
3. Group overlapping corridor envelopes by layer.
4. Replay only candidate copper for each bounded batch, with a justified halo
   and inclusive boundary rule.
5. Analyze batches and deduplicate by stable capture-scoped source identity.
6. Retain complete results for filtering, paging, reports, and selection.

Do not pair vias independently per tile. Current policy includes greedy global
pairing, traversal ordering, and global aggressor `seen` behavior in
`CorridorAnalyzer.cs` around lines 142-186. Naive tiling can change results.

The scalable path must preserve:

- disconnected/unassigned copper;
- vias spanning layers;
- long traces crossing several tiles;
- shapes and planes crossing tile boundaries;
- contour holes/voids when detailed evidence is requested;
- missing or bounded-out facts as incomplete/unknown, never clear.

First establish equivalence to the current algorithm. Treat any desired algorithm
change as a separate product-policy decision.

### Stage 4: optimize repeated query and display work from measurements

Only after the bounded workflow is correct, evaluate:

- reusing an immutable capture-scoped spatial index across replays;
- a two-level page/root interval index instead of rescanning every root;
- batching corridor envelopes to minimize repeated page reads;
- viewport plus analysis-halo prefetch off the UI thread;
- display level-of-detail for overview rendering while engineering analysis uses
  authoritative geometry;
- coverage-aware query planning that requests scalar facts, region geometry, or
  contours according to the operation;
- prepared plane interior/void summaries only where uniformity is proven.

Page-bounded transport alone does not guarantee a responsive native capture.
Historical Shockwave evidence recorded a 31.78-minute contour-rich capture in
`/mnt/c/e2studio/allegro-bridge/docs/evidence/P0-C-DENSE-CAPTURE-EVIDENCE.md`
around line 86. This is not a Nisqually timing prediction, but it means capture
phases and Allegro responsiveness must be measured separately.

Cooperative native continuation across UI turns would require a proven board
revision fence and no retained stale DBIDs. It is a later option, not the first
fix.

## Optional-adapter warnings: separate diagnosis

The warning source is the resident loader in
`src/AllegroBridge.Host/Skill/Resident/pd_allegro_bridge.il`, starting around
line 224. It probes legacy functions, environment overrides, and adjacent `.il`
files.

The Preview.104 SDK package target installs only:

- `pd_allegro_bridge.il`;
- `pd_constraint_observer.il`;
- `pd_custom_extensions.il`.

See `pack/buildTransitive/CircuitHub.AllegroBridge.Sdk.targets` around line 8.
The observed installation also contains those three files. Optional legacy
adapter absence is therefore a plausible explanation, but each warning must be
classified as:

- expected optional capability absence;
- required packaged file missing;
- failed load of an available file;
- wrong search path/version.

Do not copy arbitrary historical adapters into the installation. Package the
supported set, report actual capabilities, and suppress a warning only if that
absence is intentionally supported. The object capture ran far enough to reach
its object quota, so no evidence currently links these warnings to the catalog
failure.

## Required tests and acceptance

1. Hidden startup on Nisqually connects without an implicit full-board capture;
   AI Workflow reveals the same companion.
2. The exact triggering query and exhausted quota are retained in correlated
   evidence on the next reproduction.
3. The ordinary DP Via Corridor Run action completes above 100,000 physical
   objects, owning sealed acquisition and bounded replay through public Engine
   APIs without a PD SDK dependency or a special-page prerequisite.
4. At least one bounded typed replay and selected-object/finding journey works
   from the sealed store.
5. Small-board whole-board corridor output is equivalent between the existing
   and scalable paths, including ordering-sensitive pairing/deduplication.
6. Tile-edge shapes, long traces, disconnected copper, vias, holes, and boundary
   intrusions are retained.
7. Changed board/net/component identifiers preserve equivalent geometric output;
   a deliberately moved nonintersecting obstacle is the negative control.
8. Over-budget, cancelled, corrupted-page, incomplete-family, and board-change
   cases fail visibly without clear-result claims or edit authority.
9. Memory/time telemetry separates native traversal, page transfer, Engine import,
   index/replay, managed analysis, and drawing.
10. Existing small-board workflows, hidden startup, reconnect/recovery fencing,
    and installed restart remain working.
11. The original Nisqually board is byte-unchanged after any authorized native
    test.

Initial warm interaction targets may use the existing Engine guidance of UI work
p95 <= 5 ms/p99 <= 10 ms and input-to-visible feedback p95 <= 25 ms/p99 <= 50 ms.
These are measurement targets, not claims of current performance. Choose bulk
capture duration and memory acceptance after the first instrumented baseline and
an explicit workstation resource budget.

## Remaining native acceptance procedure

1. Prepare a new immutable verified candidate after source repairs and required
   managed gates complete. Preview.114 and every earlier candidate are excluded
   from final acceptance; preserve their packages and evidence. Do not overwrite
   or rebuild an existing version. Verify the installed hashes against the new
   package and create a fresh disposable copy equal to the protected original.
2. Open that disposable copy, always choose **Yes** on Allegro's compatibility /
   revision prompt, and prove hidden startup, reconnect, and AI Workflow activation
   without an implicit full-board capture.
3. Run `artifacts/native/ordinary-corridor-campaign.ps1`. It first cancels the
   ordinary Run action after observing actual capture work, then repeats that
   same Run action to completion. It requires confirmed structured cancellation
   cleanup, no new partial store/report, a surviving exact process pair, a sealed
   capture above 100,000 physical objects, matching capture fingerprints, full
   replay batch completion and bounded materialization. It then revalidates
   ordinary findings and exercises both embedded-document modes. Never use the
   special Large-board capture page as a prerequisite or product acceptance.
4. Retain the exact request/quota correlation, physical-object count, memory,
   phase telemetry, cleanup receipt, and screenshots. Include a known native
   shape with a hole/void where available. Use `-RequireShapeHole` when a known
   representative finding must be demonstrated. Without that switch, a bounded
   candidate search that finds no hole is recorded as unobserved, not proof that
   the board has no holes. Inspect actual application-owned screenshots: script
   receipts alone do not establish GUI visual acceptance.
5. Recheck the original board's SHA-256, size, and modification time. Any mismatch
   invalidates the campaign and must be investigated before further testing.

Campaign entry point:

- Windows:
  `C:\Users\EMILEKOWALSKI\Desktop\boards\PD-Simple-GitHub\artifacts\native\ordinary-corridor-campaign.ps1`
- WSL:
  `/mnt/c/Users/EMILEKOWALSKI/Desktop/boards/PD-Simple-GitHub/artifacts/native/ordinary-corridor-campaign.ps1`
- Mandatory parameters: `-BoardPath` (fresh disposable copy),
  `-OriginalBoardPath` (protected Nisqually original), `-EvidenceDirectory`,
  `-EvidencePrefix` (unused candidate/run prefix), and `-ExpectedEngineVersion`
  (the exact new `1.13.0-preview.<number>` version). The script rejects versions
  at or below Preview.114, existing evidence destinations, other live Allegro or
  PD Simple sessions, and an original board passed as the disposable target.
- The campaign serializes heavy operations and holds an exclusive campaign file
  lock. Evidence writes use create-new semantics. It never terminates processes;
  a failed run leaves its exact owned PIDs available for scoped inspection.
  The complete-action `dp-via-corridor-run` receipt is correlated with the sealed
  acquisition and report's `AcquisitionCorrelationId`; a completed scan alone
  cannot establish report/result completion. Native-mode, embedded-mode, and
  selected-finding Allegro screenshots use only the owned application windows
  and must be inspected for actual rendered content.
- `preview113-startup.ps1`, `preview113-activate.ps1`, and
  `reconnect-current.ps1` are version-neutral helpers despite their historical
  names. Startup resolves `--bridge-dir` from the launched companion and requires
  a matching resident `process_id`; no padding is guessed. It also rejects the
  native crash message even when a modal crash dialog keeps the process alive.
  Reconnect uses owned UI Automation invocations without global input injection.
- `preview113-large-capture.ps1`, `preview113-replay.ps1`,
  `preview113-corridor.ps1`, `preview113-cancel.ps1`,
  `embedded-board-document.ps1`, and `corridor-shape-hole-navigation.ps1` remain
  diagnostic explicit-page journeys. Their output is not ordinary-workflow
  acceptance and cannot close the incident.
- Preparation validation: all eleven native PowerShell scripts parsed under
  Windows PowerShell on 2026-09-22. Only the parser was executed; no campaign,
  GUI, Allegro session, packaging, installation, or board mutation was run during
  this harness preparation. Runtime automation and final native acceptance
  remain unverified until the new candidate campaign is executed.

Reopen the diagnosis if the retained next-run evidence identifies a different
query, quota, or deployed binary than this source chain.
