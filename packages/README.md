# Packaged PCB SDK dependency

This public source checkout does not authorize SDK redistribution. The local
`packages/` directory is a private acquisition cache: only this README and the
active bundle manifest belong in the public source index. The cache contains only
the active generation. Earlier tracked manifests remain available in Git history;
removing old files from the checkout does not erase prior public history.

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

PD consumes the exact **2.0.0** generation selected in
`Directory.Build.props`. Obtain the approved candidate from the release owner's
handoff, or the published `v2.0.0` release when that release is available.
`release-bundle.3.0.0.json` is the unchanged upstream identity and hash record
for the bundle's immutable packages. PD references these, and restores the
bundle packages they depend on:

- `PcbEngine`
- `PcbEngine.Native`
- `PcbEngine.Wpf`
- `PcbEngine.Wpf.NativeHost.Integration`

Acquire exactly the required private assets:

```powershell
gh release download v3.0.0 --repo elkowalski1989/allegro-bridge --dir packages `
    --pattern '*.nupkg' --pattern 'release-bundle.3.0.0.json' `
    --pattern 'pcb-engine-starters.3.0.0.zip'
```

Verify the manifest SHA-256 against the release owner's independently supplied
digest. The manifest establishes the exact clean SDK source commit, signing
identity, Host and package hashes, credential inclusion, license enforcement,
and native qualification. A candidate whose qualification is `not_run` has not
passed native acceptance. If `license_enforcement` is `disabled`, no key is
needed; leave `PD_SIMPLE_LICENSE_PROVIDER` unset, or select `bundled`.

The matching `pcb-engine-starters.3.0.0.zip` is bound by the manifest's SHA-256 and
exact entry inventory.
Source packaging selects only the packages PD references and the bundle
packages they depend on, this manifest, this README, and the matching starter
archive.

The previous `release-bundle.1.14.4.json` remains an unchanged historical
record. The release coordinator's PD `9cd8c68` qualification against that
generation, including installer and constraint recovery checks, does not
qualify this SDK 2.0.0 migration. Older native evidence remains attached to its
original candidate; do not relabel its manifest or package bytes.

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

No SDK source checkout, linked source, or project-reference fallback is
required. Keep all package dependencies, Host, resident files, and optional
native shell at the same generation. A DLL update does not upgrade an already
running resident. Changed package bytes require a new version; do not rebuild
or overwrite a staged generation.

Obsolete development manifests and the earlier 1.14.2 release manifest have
been removed from this checkout. Historical evidence in the documentation still
refers to its original generation; obtain any older dependency payload privately
if you need to rebuild that generation.

`PD-Simple-Source.zip` intentionally includes the active private dependency
payload so approved internal recipients can build it independently. It is not a
public-source Git archive and must stay in private distribution and CI storage.
