# Development candidate acceptance

## State of this delivery

The source now wires both real application tools through public Engine APIs.
The reusable C# module, Explorer, package versions and overlay recovery changes
compile together. The matching Preview.26 candidate passed the retained targeted
Engine/WPF Allegro 25.1 regression, including public drawing, complete live Engine
capture, composition, and focused WPF performance. Broader native, performance,
fault-injection, physical-display, and partial-fact obligations remain separately
scoped. This remains an unsigned local candidate, not an assertion of native
parity on every existing board.

The old consumer SKILL is retained as source-only comparison material. It is
not copied/loaded by the normal application package. Do not manually load it into
a session and then claim the new standard operations are self-contained. Use a
fresh Allegro process when checking absence of obsolete application procedures.

The SDK's local development-bundle script requires an existing server PUBLIC
verification-key bundle on the Windows builder. No credential/signing substitute
is provided. Older host/resident files do not match the `1.13.0-preview.26` package
set. All native/GUI checks below must use the assembled matching candidate.

## Portable source checks

With the matching local packages available:

```powershell
python scripts/check-engine-boundary.py
dotnet run --project tests/PD.EngineBoundaryChecks -c Release
dotnet build src/PD.Simple/PD.Simple.csproj -c Release
dotnet run --project tests/PD.Simple.Checks -c Release
dotnet run --project tests/PD.PcbTools.Checks -c Release
dotnet run --project tests/PD.Simple.DrawingChecks -c Release
dotnet build samples/BoardExplorer/BoardExplorer.csproj -c Release
dotnet build samples/ReadPcb/ReadPcb.csproj -c Release
dotnet build samples/PickAndMeasure/PickAndMeasure.csproj -c Release
dotnet build samples/IndependentAnalysisExtension/IndependentAnalysisExtension.csproj -c Release
dotnet build samples/ViaProximityExplorer/ViaProximityExplorer.csproj -c Release
dotnet build samples/ViaProximityExplorer/ViaProximityExplorer.Presentation.csproj -c Release
```

These commands are package-only when `AllegroBridgeSourceRoot` is absent. The
boundary script rejects ordinary direct lower-SDK source/project dependencies,
and `PD.EngineBoundaryChecks` inspects the emitted `PD.PcbTools.dll` and
`PD.Simple.dll` assembly references. Transitive SDK runtime assets supplied by
Engine are not ordinary PD coordination and are verified separately as part of
the exact candidate generation.

A maintainer without the packaged host can append
`-p:AllegroBridgeSourceRoot='<actual Bridge checkout>'` for compile-only checks.
That mode is expressly rejected by distribution targets. It does not install
or invoke Allegro. The 72 initial pure tool checks cover changed identifiers,
clicked-endpoint/truncation policy, net conflicts, widths, unit conversion,
module scope, large input arrays, incomplete-data warnings, classification,
and fresh navigation-witness rejection. The existing result/feedback/connection
gate remains useful for its original contract; it is not native migration proof.

The Bridge Windows desktop regression gate tests owned off-screen windows,
region-shaped occlusion, invalidation and projection; it is not an Allegro test.

## Operator checks on a disposable board

### Board Explorer

Discover/attach the intended session with two boards open. Find a component,
inspect string pin names and a disconnected pin, follow a net through its Xnet
and declared pair, and highlight/zoom each supported target. Test an unplaced
component, a missing target, unavailable connectivity and board close/reopen.
Copy a recipe and compile it from the package without PD-Simple references.
Unavailable pair data must not erase otherwise available component/pin data.

### Corridor

Run the existing known board/options, compare the actual findings and report
with the retained native reference, and review discrepancies rather than changing
expected output to make the candidate pass. Check module filtering, unused pairs,
filtering, first/last finding navigation, reports and raw/annotated exports.

Test more than one native response, per-layer pad differences, blind/backdrilled
vias, unknown/out-of-date backdrill data, arcs, shapes with holes, unnetted copper
and physically equivalent mil/mm boards. The reference screening approximations
are explicit; this migration does not approve them as accurate copper analysis.
Input order can affect greedy ties and global de-duplication, so native ordering
parity is specifically unverified. No missing-data case may say clear/pass.

After an analysis, alter a finding object or its pad/layer state. Navigation must
refuse changed/ambiguous witnesses and request a fresh analysis. A serialized
report or edited offline capture must never acquire native navigation authority.

### Point-to-point trace

Check clicked rather than centered endpoints, one-segment/equal-coordinate
cases, horizontal-first bends, widths/units, one known or conflicting endpoint
nets, visible/active layers, netless boards and no usable ETCH layer.

Exercise Clear first pick and Cancel before a handle arrives, while picking,
between picking and edit dispatch, and during the native edit. There must be no
automatic mutation replay. Create and Undo the specific trace with an unrelated
control object present. A failed operation retaining recovery must keep Undo;
an unverified outcome must block new work and must not display a success label.
Test closing the application during a pending native operation separately from
cancelling only its managed wait.

### Overlay and capture

Navigate a known finding, then start/cancel and complete screenshot selection.
Start/stop full-display sharing and inspect both local and receiver views. Test
window-only sharing separately because it may omit a separate owned overlay.
After a temporary obstruction, drawing must resume only from a fresh valid view.
Test partial covers and region-shaped sharing borders, zoom/pan, resize,
minimize/restore, and transfers between existing differently scaled monitors.
Old pixels must clear after actual board/session loss. No screenshot/sharing
failure may create, cancel or replay copper edits.

Record the exact package hashes, board/options and capture application/mode for
any defect. Do not change security settings or invent pixel tolerances to pass.
