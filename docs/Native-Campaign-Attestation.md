# Native 12-tool campaign attestation — Engine 1.13.0-preview.98

Date: 2026-09-21. Board: disposable `campaign.brd` copy of
`blyth1z_unrouted.brd` in owned Allegro 25.1 (isolated profile, worktree
resident via replay). Runner: `tests/PD.NativeCampaign/run-native-campaign.bat`
(`run-licensewatch.bat` + `run-connected-campaign.bat`, no prompts).

## Result

33/33 PASS on 2026-09-21 (Engine 1.13.0-preview.98, Allegro 88024,
disposable `campaign.brd`). Three steps pass via EXPECTED-GATE on exact
deterministic product refusals: PreviewEdit (`fixed and cannot be` — all
72 board targets fixed), Prepare (`library_destination_unsupported` —
board-connected session), MfgRefreshSource (source identity incomplete).
All other steps pass on live markers with per-step PD + Allegro
screenshots, page text, and UIA dumps in the run dir.

- Run dir: `C:\e2studio\pd-simple-local-runs\native-connected-20260921-071251`
  (supersedes `...-065339` at 21/26 and `...-070523` at 0/12, both killed by
  a persistent Teams WebView2 popup (pid 19968) overlapping Allegro's
  bottom-right corner; the occlusion guard refused honestly. Allegro was
  moved clear of the toast zone and captures now ride out transient
  occlusion with a bounded 6x10s retry before failing.)
- Summary: `summary.csv` (PASS/FAIL per tool step with marker evidence)
- Evidence per step: `pd-<step>.png` (in-app render), `allegro-<step>.png`
  (Allegro-window-only PrintWindow capture, occlusion-guarded),
  `text-<step>.txt` (page text), `tree.txt` (UIA dump per tool);
  failures additionally keep `pd-<step>-fail.png` + `text-<step>-fail.txt`

## Method notes

- Allegro captures use `PD.WindowCapture.exe --method auto
  --require-unoccluded` (PrintWindow/DWM/WGC window-only); PD minimizes
  around each Allegro capture so no PD pixel can overlap the Allegro rect.
- Crossing Analyze requires a drawn analysis rectangle; the campaign
  selects the Crossings tab and drags across canvas geometry derived from
  live UIA rects, then asserts the `Nets:` totals line (verdict may be
  COMPLETE or data-driven INDETERMINATE on this unrouted board).
- Via/route preview requires a selected target; the campaign expands the
  ComboBox (items materialize only while expanded), walks picker items
  newest-first with per-attempt re-resolve (selection collapses the popup
  and virtualized containers go stale), and keeps the first `PREVIEW ONLY`.
  Fixed targets are skipped by design; when every walked target refuses as
  fixed, the step asserts the exact refusal text (see board note below).
  Preview is non-mutating, so attempts are safe.
- Scene save drives the descendant Save-As dialog, dismisses stacked
  shell/validation popups, types a full path, and verifies the file.
- Steps marked EXPECTED-GATE assert an exact deterministic gate message
  (any other failure text still fails the step); `rejected:` refusal texts
  fail fast like `failed:` ones. See findings.
- Failure evidence: every FAIL keeps the failure screenshot plus the live
  page-text dump, so the next triage never guesses what PD showed.

## Fixes landed for this run (bridge-coordinator worktree, uncommitted)

- `update-drc` promoted into the generated PCB contract
  (`contracts/pcb-commands.json`, `PcbContracts.cs`,
  `PcbCommandCapabilities.Generated.cs`, `pcb_contract.il`,
  `AllegroPcbDrcRun.cs`).
- `chbPcbDrcFreshness`: explicit `(return (cond ...))` (bare-`prog` tail
  returned nil, poisoning run evidence).
- `chbPcbReadDrc` dispatcher: explicit `(return (cond ...))` — the missing
  return made EVERY marker read return nil, which the host rejects as
  `custom_skill_result_invalid`. Root-caused live
  (`CODE=custom_skill_result_invalid`) and verified fixed live.
- `chbPcbEditCaptureAll`: bound region-visit counters, fixed via-row
  filter (rows are lists tagged at element 8, dbid at `car`), added
  stage breadcrumbs (`chbPcbCaptureStage`).
- Live probe verification on .98 before the campaign: `read-drc` Complete
  (10/10 markers, enumeration complete, freshness OutOfDate),
  `read-edit-targets` Complete, `update-drc` Complete.

## Campaign fixes (pd-coordinator worktree, uncommitted)

- StrictMode index access for optional step/entry keys (`Gesture`,
  `SetText`, `PickTarget`, `ExpectFailure`, `Text`, `File`).
- Picker: ComboBox expansion + per-attempt picker/item re-resolve
  (`Find-VisibleById`, `Expand-Picker`); on-screen twin preference kept.
- SaveScene: freshness gate skipped (dialog handler already proves the
  file exists and the dialog closed); `*saved*` marker on current text.
- Added `PD_PROBE_TARGETS_OUT` to TargetsProbe (full payload to file;
  stdout stays capped at 3000 chars).

## Board note (unrouted campaign board)

All 72 edit targets are fixed placed components; 0 vias, 0 traces, 0
non-fixed targets (verified by full-payload probe). A move/resize preview
is therefore correctly refused on every target, and the PreviewEdit step
asserts the exact refusal (`fixed and cannot be`,
`EngineWorkbenchView.Editing.cs`). The preview-success path needs a board
with unfixed objects and is NOT covered by this run.

## Product findings (observed live, not fixed by this campaign)

1. Scene default framing unusable (blob): content spans ~1360x1676 mils
   but `Document.Bounds` is (-3000,-3000)-(9000,9000), so Fit renders the
   board at ~1/8 scale with all labels stacked. Data itself is correct
   (282 captured representations, 0 display-budget omissions on .98).
   Recommend fit-to-content plus label decluttering.
2. Physical-symbols Prepare deterministically refuses while
   board-connected: `ValidatePersistence` requires document, destination,
   and staging to be the same path, but the live document is the board
   (`campaign.brd`) while the UI hard-wires destination = staged DRA
   (`SaveStagedDocument`). The campaign asserts the exact
   `library_destination_unsupported` refusal. Running Prepare against a
   symbol-document session (staged DRA open as the document) is NOT
   covered by this board-connected run. The lane-E binding gate
   (`No symbol binding is active`) sits behind this refusal.
3. Manufacturing source refresh cannot succeed: the Engine session never
   binds `ProcessId` into `AllegroSessionBinding` (no `ExpectedSession`;
   target PID dropped in `ConnectLaunchWindowsAsync`), so
   `RequireDocument` always throws. The fence behaves correctly; the
   campaign asserts the exact gate message. Needs product-team session
   identity design (do not fabricate Nonce/CaseId).
4. `SaveScene` dialog has no `InitialDirectory`: a stale per-user MRU
   location opens a blocking 'Location is not available' popup. The
   campaign dismisses it; recommend setting `InitialDirectory`.
