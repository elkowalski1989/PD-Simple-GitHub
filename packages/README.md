# Packaged Engine dependency

This public source checkout does not authorize SDK redistribution. The local
`packages/` directory is a private acquisition cache: only this README and bundle
manifests belong in the public source index. Historical tracked packages require
an explicit source-index cleanup; ignore rules do not remove them or erase prior
public history. Keep existing local bytes unchanged during that cleanup.

Approved internal recipients obtain the exact generation from the private
`elkowalski1989/allegro-bridge` release or the release owner's internal handoff.
Copy only the six packages, original manifest and declared starter archive named
by that manifest into this directory, then verify their SHA-256 values against
the approved manifest before restore. Do not put access tokens in `NuGet.Config`
or source files. Public nuget.org is only for mapped third-party dependencies.
The diagnostic `.7` generation below is an internal handoff, not a published tag.

For published generations, authenticated `gh release download` against that
private repository can acquire the exact manifest and individually named assets;
select the approved version explicitly, never `latest`. Existing private Bridge
CI instead builds one generation and injects it into the exact public PD source
revision selected by `build/pd-consumer.json`. No private credential or binary is
needed by the public PD source-check workflow.

PD consumes the exact **1.14.2** generation selected in `Directory.Build.props`: the signed private
release `v1.14.2` of `elkowalski1989/allegro-bridge`. `release-bundle.1.14.2.json` is its upstream identity
and hash record for these six immutable packages:

- `CircuitHub.AllegroBridge.Sdk`
- `CircuitHub.AllegroBridge.Engine.Core`
- `CircuitHub.AllegroBridge.Engine`
- `CircuitHub.AllegroBridge.Wpf`
- `CircuitHub.AllegroBridge.Wpf.NativeHost`
- `CircuitHub.AllegroBridge.Wpf.NativeHost.Engine`

Download them with `gh release download v1.14.2 --repo elkowalski1989/allegro-bridge` into this folder.
The manifest's SHA-256 is `7eb1266e660598d2858159a329dc146109f1ae1b47e713daf490634b8acfb1a6`. It records source commit
`f634e6d95ca72b8a37d168003fc2489d28e1c71c`, clean source, and a verified Authenticode signature under thumbprint
`110020c143adf7ab85b120e6b2b0c1f9c573e4f2`. Its `license_enforcement` is `disabled`: this Host grants
itself a local lease and needs no license key, so leave `PD_SIMPLE_LICENSE_PROVIDER` unset (bundled). Native
qualification of this generation is in the release's `release-acceptance.1.14.2.json`; this dependency pin
does not by itself establish PD release acceptance.

The matching `allegro-engine-starters.1.14.2.zip` has SHA-256
`b9f824fefe38c3e4117f46c1f39e495c30134255e32dda76ea82dc4ce175a3e8`.
Source packaging selects only the active six packages, this manifest, this README, and the matching starter
archive: nine package-directory files.

Use .NET SDK 10 for PD and the ordinary Engine/WPF projects. Windows x64 is
required for PD, the packaged Host, and native integration. Standalone
NativeHost targets .NET 9 Windows; Engine/Core use .NET 10, and WPF plus the
optional Engine-aware adapter use .NET 10 Windows. Native hosting additionally
requires the matching packaged shell payload. Qualified Cadence, DPI, shell,
and live GUI behavior must be checked for the exact assembled candidate.

Restore from the repository's `NuGet.Config`, which selects the local package
folder plus the configured public dependency source:

```powershell
dotnet restore src/PD.Simple/PD.Simple.csproj --configfile NuGet.Config
dotnet build src/PD.Simple/PD.Simple.csproj -c Release --no-restore
```

No Bridge source checkout, linked source, or project-reference fallback is
required. Keep all package dependencies, Host, resident files, and optional
native shell at the same generation. A DLL update does not upgrade an already
running resident. Changed package bytes require a new version; do not rebuild
or overwrite a staged generation.

Older packages and manifests remain historical records and are excluded from
the active source archive. The retained `.183` generation and its original
manifest are unchanged. That historical candidate contained a bundled license
credential, and its declared upstream starter archive was not staged here.

`PD-Simple-Source.zip` intentionally includes the active private dependency
payload so approved internal recipients can build it independently. It is not a
public-source Git archive and must stay in private distribution and CI storage.
