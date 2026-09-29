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

PD consumes the exact **1.13.0-preview.readiness.20260928.7** generation selected in
`Directory.Build.props`. `development-bundle.1.13.0-preview.readiness.20260928.7.json` is the
original upstream identity and hash record for these six immutable packages:

- `CircuitHub.AllegroBridge.Sdk`
- `CircuitHub.AllegroBridge.Engine.Core`
- `CircuitHub.AllegroBridge.Engine`
- `CircuitHub.AllegroBridge.Wpf`
- `CircuitHub.AllegroBridge.Wpf.NativeHost`
- `CircuitHub.AllegroBridge.Wpf.NativeHost.Engine`

All six packages and the declared starter archive were verified against that
manifest before additive staging. Its SHA-256 is
`05079e29a023a99fb390a099c8d7df7064d438f65181fcbd71c4eee99d018de3`.
The manifest records source commit `4bd44c563c068790da180b17adb2ffacd7f94a14`,
modified source, and source-diff SHA-256
`4d6d81bd51027ebaf4a0c77a6908bcc4258d2bb39ba42baba85c044c73a7a4cd`.
This is an **unsigned diagnostic development candidate without a bundled
license credential**. Its original live Allegro and native GUI qualification
fields remain `not_run`; the manifest is retained unchanged. Package validation
and this dependency pin do not establish production-release acceptance.
Runtime access requires an explicitly selected supported license provider.

The matching `allegro-engine-starters.1.13.0-preview.readiness.20260928.7.zip` is
included with its exact declared inventory and SHA-256
`069988efb67dda005becae3c4e3b6ae9caf18904ef368f931df57f8e6368e687`.
Source packaging selects only the active six packages, this manifest, this
README, and the matching starter archive: nine package-directory files.

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
