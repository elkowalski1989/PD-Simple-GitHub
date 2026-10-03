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

For published generations, authenticated `gh release download` against that
private repository can acquire the exact manifest and individually named assets;
select the approved version explicitly, never `latest`. Existing private Bridge
CI instead builds one generation and injects it into the exact public PD source
revision selected by `build/pd-consumer.json`. No private credential or binary is
needed by the public PD source-check workflow.

PD consumes the exact **1.14.4** generation selected in
`Directory.Build.props`, from the signed private Bridge release
[v1.14.4](https://github.com/elkowalski1989/allegro-bridge/releases/tag/v1.14.4).
`release-bundle.1.14.4.json` is the unchanged upstream identity and hash record
for these six immutable packages:

- `CircuitHub.AllegroBridge.Sdk`
- `CircuitHub.AllegroBridge.Engine.Core`
- `CircuitHub.AllegroBridge.Engine`
- `CircuitHub.AllegroBridge.Wpf`
- `CircuitHub.AllegroBridge.Wpf.NativeHost`
- `CircuitHub.AllegroBridge.Wpf.NativeHost.Engine`

Acquire exactly the required private assets:

```powershell
gh release download v1.14.4 --repo elkowalski1989/allegro-bridge --dir packages `
    --pattern '*.nupkg' --pattern 'release-bundle.1.14.4.json' `
    --pattern 'allegro-engine-starters.1.14.4.zip'
```

The manifest SHA-256 is
`c4a5cd07c2665672d0d07289bdc858cc7cc820a57e86138af3747562ef8aee38`.
It records clean source commit
`6b9ed1c149021e0adb305f793293e6f990c3a356` and verified upstream signing
under certificate thumbprint `110020c143adf7ab85b120e6b2b0c1f9c573e4f2`.
Its `license_enforcement` is `disabled`: no license key is needed. Leave
`PD_SIMPLE_LICENSE_PROVIDER` unset, or select `bundled`.

The matching `allegro-engine-starters.1.14.4.zip` has SHA-256
`1245d18dd0591841b775604f92eef19e0a06cd795d082909c2151d18f7f32bff`.
Source packaging selects only the active six packages, this manifest, this
README, and the matching starter archive: nine package-directory files.

Bridge 1.14.4 refreshes the WPF Recovery coordinator while tracked work is
active, pages component reads above 4,000 pins, bounds large-board region
traversals, and supports observations of empty boards. These are package-owned
changes. The release coordinator qualified PD commit `9cd8c68` against 1.14.4,
including build/source archives, installer install/rollback/tamper/uninstall,
and the published-PD constraint case: Prepare 7, Execute 7 to 8, Recovery-tab
recovery 8 to 7 with the source board unchanged. That evidence belongs to the
coordinator's exact PD build; it does not qualify this older checkout or every
native/GUI scenario. The original manifest's native qualification fields remain
unchanged.

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
