# Engine candidate integration

This final local integration pins SDK/Engine/WPF **1.13.0-preview.7** from the
final Lane 10 Bridge commit. The versioned development-bundle manifest is the
package provenance record; this source text is not native qualification. The
[Board Explorer](samples/BoardExplorer/README.md) consumes typed live and offline
Engine; the production app hosts the typed shared Workbench and uses canonical
Engine drawing for the live corridor overlay. There is **no separate studio**.
Read [Engine-Acceptance.md](docs/Engine-Acceptance.md) before native testing.
The [Engine product-adoption note](docs/Engine-Product-Adoption.md) records session,
policy, drawing, lifecycle, remaining SDK seams, and verification ownership.
Earlier version-specific evidence below remains historical, not qualification
of this source candidate or a newly built package.

# PD Simple

A C# PCB-tool reference using matching Allegro Bridge SDK, Engine, and WPF
packages. Package-only compilation does not claim licensed native acceptance.

- **DP via corridor:** read one coherent board capture, screen it in the reusable
  C# module, review findings, revalidate a finding before native navigation, and
  export native/annotated images and reports.
- **Point-to-point trace:** choose width, pick actual endpoints, plan a horizontal
  first path in C#, create/read back natively, and Undo that particular edit.
- **Board Explorer sample:** discover and attach to an intended running board,
  inspect nets/components/pins/declared pairs/layers, highlight/zoom a named
  object, and copy the public C# query. It is separate from the two-tool menu.

The SDK owns session/board identity, native operations, authorization, live
canvas observation and WPF drawing. `PD.PcbTools` owns engineering policy.
`PD.Simple` owns the application workflow and presentation. No consumer SKILL is
loaded by the current standard-operation path. The old implementation remains
source-only under `src/PD.Simple/Skill` for native comparison, not as an automatic
fallback or a second bridge.

## Build the matching candidate

Use the exact **1.13.0-preview.7** SDK, Engine, WPF, protected Host, and resident
generation together. Do not mix older packages or runtime files with this source.

From the `allegro-bridge` checkout:

```powershell
.\scripts\build-development-bundle.ps1 `
  -Version '1.13.0-preview.7' `
  -SigningKeysPath '<existing server public verification-key bundle>' `
  -ConsumerRoot '<actual PD-Simple checkout>' `
  -AllowUnbundledLicense
```

That produces matching local SDK/Engine/WPF packages and can copy them to `packages/`
without overwriting a different package with the same version. It does not sign
or publish a release, install anything, create credentials, change licensing, or
embed a private license key. .NET 10 and the NativeAOT C++ toolchain must already
be available on Windows. The runtime retains its normal entitlement checks.

Then, from this checkout:

```powershell
.\Build.cmd
```

The build creates a fresh `artifacts/build-*/PD-Simple` payload and verifies the
installer manifest. It also creates the setup/source archives, retaining earlier
archives rather than overwriting them without recovery. Run the included
installer deliberately, open the intended board in Allegro and enter `pd_simple`.
The existing connection chooser can attach to another SDK-verified resident.
Changing the connection never replays an operation or clears unresolved edit
outcomes just to enable more work.

For **compilation only**, maintainers can use an adjacent SDK source checkout:

```powershell
dotnet build src/PD.Simple/PD.Simple.csproj -c Release `
  -p:AllegroBridgeSourceRoot='<actual SDK checkout>'
```

This mode copies no protected host/resident, writes `COMPILER-ONLY.txt`, and
rejects `Publish`/`Pack`. It is not a runnable package or an entitlement bypass.

## Start with a small example

| Example | Purpose |
| --- | --- |
| [BoardExplorer](samples/BoardExplorer/README.md) | Read-only board navigation and copyable public C# recipes. |
| [ReadPcb](samples/ReadPcb/README.md) | Explicit bounded geometry read with coverage diagnostics. |
| [PickAndMeasure](samples/PickAndMeasure/README.md) | Measure two clicked positions without creating copper. |

Every example uses public package APIs, not PD-Simple internals. The SDK also
contains small `allegro-console` and `allegro-wpf` .NET templates. The complete
application is an advanced reference, not the code a beginner must copy.

```csharp
var pcb = await session.OpenPcbAsync(cancellationToken);
var read = (await pcb.InspectComponentAsync("U12", cancellationToken)).RequireCatalog();
var component = read.FindComponent("U12");
foreach (var pin in read.Pins.RequireAvailable())
{
    Console.WriteLine($"{pin.Number}: {pin.Net ?? "Disconnected"}");
}
```

Unavailable, not requested, and available-empty are different states. Part
numbers are not component identities. Pin names are strings. Declared
Side A/B pair members do not identify positive/negative polarity.

## Where the implementation lives

| Source | Responsibility |
| --- | --- |
| `src/PD.PcbTools/HorizontalFirstPlanner.cs` | Clicked-coordinate, width, net and layer policy. |
| `src/PD.PcbTools/CorridorAnalyzer.cs` | Pure managed screening of SDK-acquired data. |
| `src/PD.PcbTools/CorridorNavigation.cs` | Fresh finding witnesses before SDK-owned native revalidation. |
| `src/PD.Simple/BridgeSession.cs` | One connection/admission/cleanup owner. |
| `src/PD.Simple/BridgeSession.PcbTools.cs` | Complete typed tool workflows and report writing. |
| `src/PD.Simple/BoardOverlayDrawingPolicy.cs` | PD corridor labels/colors expressed as canonical Engine drawing intent. |
| `src/PD.Simple/BoardOverlayController.cs` | Product orchestration and route HUD around shared WPF drawing presentation. |

The corridor remains a screening tool, not clearance analysis or an SI sign-off.
Its named reference policy preserves nearest-via pairing, first-target-layer pad
width, arc chords, centerline/center-point tests and shape bounds. Those are
explicit approximations, not exact copper contours. Missing backdrill or required
pad data is reported as **review required**, never a zero-findings pass. Native
comparison of this managed candidate remains required, especially ordering ties,
layer-dependent pads and equivalent-unit cases.

## Verification and screenshots

See [Development acceptance](docs/Development-Acceptance.md) for exact commands,
coverage and the remaining operator checks. Compiling the code does not prove
native routing, existing-board parity or rendered screenshot/share behavior.

The overlay now survives nonterminal observation interruptions and uses batched
point projection. The SDK also respects explicit region-shaped sharing borders
while preserving conservative clipping. A full-screen snipping surface may still
legitimately hide the live overlay until a fresh view is available. A window-only
recording may omit the separate owned overlay window. Use the native/annotated
export for a stable composed image and validate the actual sharing application.
No global input injection, desktop capture, capture-application allowlist or
Windows security-policy change is introduced.

## Provenance

The retained native reference was adapted from PD Workflow Engine. Historical
native observations and limitations are preserved in [CONTRIBUTING.md](CONTRIBUTING.md).
They do not certify this new candidate. Existing notices and package licenses
remain in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) and the supplied packages.
