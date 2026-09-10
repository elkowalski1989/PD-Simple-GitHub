# PD-Simple early SDK handoff — 1.12.0-preview.1

Internal integration milestone, not a release or completion of the remaining SDK goal.
The package files are immutable: later changes must use a new preview version.

## Exact matching packages

Version for both: **1.12.0-preview.1**. WPF declares an exact dependency on this SDK version.

- Windows SDK: `C:\e2studio\allegro-bridge\_local-runs\sdk-handoff\1.12.0-preview.1\packages\CircuitHub.AllegroBridge.Sdk.1.12.0-preview.1.nupkg`
- Windows WPF: `C:\e2studio\allegro-bridge\_local-runs\sdk-handoff\1.12.0-preview.1\packages\CircuitHub.AllegroBridge.Wpf.1.12.0-preview.1.nupkg`
- WSL package directory: `/mnt/c/e2studio/allegro-bridge/_local-runs/sdk-handoff/1.12.0-preview.1/packages`

These are local unsigned development packages with a fresh Windows x64 NativeAOT
host. No Authenticode signing, public publication, installation, Git change,
license modification or certificate modification was performed. The NuGet
repository commit identifies the base checkout; the package also includes the
uncommitted follow-up changes. Do not interpret that commit as a clean source tag.

```xml
<PackageReference Include="CircuitHub.AllegroBridge.Sdk" Version="[1.12.0-preview.1]" />
<PackageReference Include="CircuitHub.AllegroBridge.Wpf" Version="[1.12.0-preview.1]" />
```

Add the package directory above as a local NuGet source. The consumer must use
the matching packaged resident files; updating only its DLL references does not
upgrade an already-running old resident.

## Reconnect board chooser

```csharp
using CircuitHub.AllegroBridge;
using CircuitHub.AllegroBridge.Windows;

// Run synchronous discovery off the WPF UI thread. Display unavailable reasons.
var candidates = await Task.Run(
    () => AllegroDesktop.DiscoverRunningInstances(), cancellationToken);

// selected is the exact candidate deliberately selected in the chooser.
AllegroDesktopAttachment next = await AllegroDesktop.AttachAsync(
    selected, cancellationToken: cancellationToken);
AllegroBridgeSession session = next.Session;
AllegroDesktopBinding desktop = next.Desktop;
// Transfer ownership of next to the consumer's session owner. Dispose the old
// consumer binding only after this succeeds. Dispose next on replacement/shutdown.
```

Native proof uses a fresh nonce-correlated response containing `ipcGetPid()`,
with a held Windows process handle and rechecked start time/session/generation/HWND.
It never uses a guessed title, launch ancestry, or a fallback window. Discovery
does not connect or load SKILL. Attachment does not load/reload SKILL, replay an
operation, or close an existing GUI or Allegro process. Disposing an attachment
only disposes that SDK client and desktop binding.

`CanAttemptAttach` is a hint, not a guarantee. Missing, duplicate, stale or changed
identity is rejected. Only already-running compatible residents are attachable.
Default discovery searches immediate `pdbridge*` directories beneath client TEMP;
exact directory hints do not expand the client's temporary-root policy. Custom
profiles need matching client temporary-root configuration. Do not create a
chooser fallback that loads a resident or selects another window silently.

## Small catalog/relationship API example

```csharp
AllegroPcbSession pcb = await session.OpenPcbAsync(cancellationToken);
AllegroPcbCatalogResult read = await pcb.ReadCatalogAsync(new(), cancellationToken);
AllegroPcbCatalog catalog = read.Catalog
    ?? throw new InvalidOperationException($"Catalog failed: {read.Receipt.Code}");
foreach (var net in catalog.Nets.RequireAvailable())
    Console.WriteLine($"{net.Name}: {net.PinCount} pins");
foreach (var component in catalog.Components.RequireAvailable())
    Console.WriteLine($"{component.Refdes}: part {component.PartNumber ?? "unavailable"}");

// Use an actual refdes from the catalog, not a part number.
var chosen = catalog.Components.Items.First();
var detail = await pcb.ReadComponentPinsAsync([chosen.Refdes], cancellationToken);
foreach (var pin in detail.Catalog!.Pins.RequireAvailable())
{
    Console.WriteLine($"{pin.ComponentRefdes}.{pin.Number}: {pin.Net ?? "disconnected"}");
    if (pin.Net is not null && catalog.Xnets.Status == AllegroPcbReadStatus.Available)
        foreach (var pair in catalog.GetDifferentialPairsForNet(pin.Net))
            Console.WriteLine($"{pair.Name}: Side A={pair.SideAXnet}, Side B={pair.SideBXnet}");
}
```

`AllMetadata` requests nets, components, declared connectivity and etch layer state,
without copper or automatic pin detail. `ReadComponentPinsAsync` batches 1–32
exact component references. `BoardContext` requests current board bounds/units
without requiring any existing net. Properties and relationship helpers are local.
Use `NotRequested` / `Available` / `Unavailable`; unloaded does not mean empty.
Components retain nonunique part numbers, unplaced components and string pin IDs.
Xnet membership is native; Side A/B never implies positive/negative polarity.

The complete independent example is in SDK package `samples/BoardCatalog`, also
at `C:\e2studio\allegro-bridge\samples\BoardCatalog` (WSL:
`/mnt/c/e2studio/allegro-bridge/samples/BoardCatalog`). No SDK project reference is used.

## Query corrections and corridor inputs

`await pcb.ReadRegionAsync(new(), cancellationToken)` now permits default/null
bounds and layers across SDK, host, native registration and resident parsing.
Optional empty/omitted lists agree; required-empty and wrong-kind lists still reject.
Explicit returned bounds are matched to the request's native-unit normalization,
without adding a geometric tolerance.

`ReadBoardInputsAsync(new(ModuleName: null, IncludeContours: false))` captures the
complete bounded net/module catalogs and all-net via/cline/shape inputs once and
assembles checked immutable pages. Module selection returns actual native module
bounds and retains foreign obstacles outside the module. Coincident objects are
not deduplicated. Exceeding capture bounds fails rather than claiming truncated
success. Read `docs/Existing-Sessions-and-Board-Inputs.md` in the SDK package for
exact limits and semantics.

## Verification and limitations

- Managed PCB/catalog/geometry gate: 198 assertions passed, including renamed
  identifiers, duplicate part numbers, string/disconnected/unplaced pins,
  multi-member Xnet relationships, empty versus unloaded/unavailable catalogs,
  paged assembly, mixed/incomplete-page rejection, and wrong-region bounds.
- Windows development-native read: passed catalogs, selected pins, defaults,
  geometry and independent-client native attachment. Missing/ambiguous/stale
  resident negatives passed, and detach preserved the Allegro process.
- Native catalog returned 38 nets, 72 components, 38 Xnets and four layers over
  two pages. The same read worked with control-only snapshots whose Nets remained
  unloaded. The native fixture had no declared differential pairs; positive pair
  relationships are covered by managed fixtures, not a new native pair fixture.
- Control-scope native guarded region navigation and stale-state rejection passed.
  The attempted fresh WPF visual regression stopped at `canvas_fully_obscured`
  before visible drawing. This is not a new GUI pass. WPF presentation code is
  unchanged; matching package compilation does not establish visible rendering.
- The large native whole-board/module fixture remains unfinished. Multi-page
  native catalogs and managed 2,051-object board assembly are proven, but do not
  substitute for that large native geometry/module test.
- Structural native-source validation and generated-contract drift check passed.
  Structural validation does not establish every native API signature.
- Exact-package acceptance: **PASS**. A Windows consumer restored only from these
  SDK/WPF packages ran with the packaged NativeAOT host and resident. Control-scope
  catalog/pin/context reads, default region, board inputs, bounded geometry, and
  independent-client native attachment passed. Missing/ambiguous/stale hint
  rejection and detach-without-closing-Allegro passed.
  Evidence: `C:\e2studio\allegro-bridge\_local-runs\pcb-api-native\read-ffd4d3f0d8fd45e881ef5188fc5a67b0`
  (WSL: `/mnt/c/e2studio/allegro-bridge/_local-runs/pcb-api-native/read-ffd4d3f0d8fd45e881ef5188fc5a67b0`).
- Independent `samples/BoardCatalog` package-only example compiled successfully
  with zero warnings/errors. The exact-package native gate exercised those same
  public APIs; the standalone example was compiled, not separately driven through
  a selected live window.

Captures are frozen data, not edit epochs or enduring navigation authority. Use
fresh focused region reads and guarded navigation before displaying findings.
Whole-board inputs do not include component-pin copper. Scalar-only reads cannot
prove copper clearance; unavailable contour/backdrill/fill details remain explicit.
Different-scaling-factor monitor transfer and exhaustive geometry variants are
not certified by this milestone. Full Explorer and both production-tool migrations
remain owned by the PD-Simple session; that checkout has not been modified here.
