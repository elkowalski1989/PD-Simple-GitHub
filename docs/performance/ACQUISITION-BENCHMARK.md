# PD acquisition and navigation benchmark

This console adapter runs the actual PD.PcbTools CorridorAnalyzer and public
packaged Engine APIs. It is not another analyzer, a cached-results provider, or
an alternative production connection path. The matching Bridge branch is
`astra/acquisition-performance-20260922`.

## Build and inventory

```text
dotnet build tests/PD.AcquisitionBenchmark -c Release
```

Normal repository NuGet configuration and pins are retained. No source-project
shortcut is added to production PD. A new Engine package is required before using
future native protocol changes; this adapter can measure the present baseline.

Create an inventory JSON object mapping stable names to absolute paths and exact
lowercase SHA-256 values:

```json
{
  "adapter": {"path":"C:/approved/PD.AcquisitionBenchmark.dll","sha256":"[REAL_SHA256]"},
  "dotnet": {"path":"C:/Program Files/dotnet/dotnet.exe","sha256":"[REAL_SHA256]"},
  "engine": {"path":"C:/approved/CircuitHub.AllegroBridge.Engine.dll","sha256":"[REAL_SHA256]"}
}
```

Include Core, SDK, dependent assemblies, PD.PcbTools, Host, resident and extensions
actually used. Placeholders intentionally fail validation. Hash inventory contents
before and after each run. A path to an unrelated Host does not prove which Host
is loaded; verify process/resident identity using the existing native gate first.

## Offline archive

```text
dotnet tests/PD.AcquisitionBenchmark/bin/Release/net10.0-windows/PD.AcquisitionBenchmark.dll --mode offline --input <EXACT_SCENE_ARCHIVE> --receipt <NEW_RECEIPT_JSON> --run-id <UNIQUE_ID> --condition reference --inventory <INVENTORY_JSON> --iterations 1
```

Offline mode loads the specified immutable .allegroscene, runs the existing
algorithm and records ordered findings and coverage hashes. It does not authorize
live navigation or edits. The adapter writes bounded metadata receipts and
streamed result JSON beside them. It refuses to overwrite existing results.

## Fresh live captures and retention

Use the same arguments with `--mode live --input <SAVED_BOARD_PATH>
--bridge-dir <AUTHORIZED_ACTIVE_DIRECTORY>`. Windows, a licensed compatible Host
and an owned disposable board session are required. Input SHA identifies saved
bytes, NOT unsaved live geometry. Every iteration calls ReadAsync again and
requires a new capture ID; it does not reuse prior analysis results.

Use `--iterations 20` for repeated captures in one native session. Results and
coverage must remain equivalent; drift fails the campaign but keeps earlier
per-iteration evidence. No forced GC or OS-cache manipulation is performed.

## Browsing versus strict checking

Add `--navigation browse --selection-count 24` to visit the first twelve findings
twice from the current capture, with zero full-region reads in the adapter path.
Use `--navigation strict` separately to measure fresh-region selected-field
verification. A strict recheck is not rerunning crossing analysis.

Each operation retains mode, finding, timings, read count and viewport evidence.
No comparison may treat the two modes as the same verification strength. The
console does not render WPF or capture review images, so it does not establish
selection-to-visible latency. Use the existing PD UI campaign for that endpoint,
rapid-reselection/backpressure, overlays, recording and focus/occlusion cases.

## Automated campaign

Use the Bridge `tools/acquisition_performance/campaign.py` runner. Its JSON plan
contains `schema=allegro.acquisition-campaign/v1`, an exact `input_file` and
`expected_input_sha256`, the same `artifacts` inventory, `repetitions`,
`timeout_seconds`, and `conditions=[{name,argv}]`.

Use an absolute executable argv[0]. Arguments are separate strings, not a shell
command. Exact argv elements `{receipt}`, `{run_id}`, `{input}`, `{condition}`
are substituted by the runner. Each condition must include the adapter's required
arguments. For same-session behavior use adapter `--iterations`; separate runner
invocations create separate managed sessions and must not be mislabeled warm.

The runner alternates condition order and stops on a failure or timeout. It only
terminates its created client, never an existing Allegro process. A timeout means
native outcome unknown and requires reconciliation before another experiment.

## Next product change

The Bridge adds generic batched spatial/field demand and within-observation memo
prototypes. These are not yet native projection. PD must define subjects/corridor
regions and consume stable all-net candidates after producer-side projection has
qualified. Preserve global seen, pairing, source ordinals, ordering and warning
semantics. Do not silently substitute an optimized engineering definition.
