# Packaged Engine dependency

Current source consumes the exact **1.13.0-preview.16** Engine generation in this
directory. `development-bundle.1.13.0-preview.16.json` is the authoritative
four-package identity and hash record. Ordinary PD projects directly reference
Engine and optional WPF; SDK/Host/resident assets remain transitive implementation
details of that same generation.

Current exact packages:

- `CircuitHub.AllegroBridge.Engine.Core.1.13.0-preview.16.nupkg`
- `CircuitHub.AllegroBridge.Engine.1.13.0-preview.16.nupkg`
- `CircuitHub.AllegroBridge.Wpf.1.13.0-preview.16.nupkg`
- `CircuitHub.AllegroBridge.Sdk.1.13.0-preview.16.nupkg` (transitive runtime owner)

These immutable files came from clean Bridge source commit
`e601f0f08207338dce9a4ea9eff6ff675b4383a9`. They are an unsigned local
development candidate, not a public release and not proof of the later integrated
P0 branches. P0-I must regenerate the exact integrated generation and repeat the
package/native gates. Do not overwrite a different package with the same version
or mix older Host/resident files with these assemblies.

`NuGet.Config` resolves the exact versions from this local folder. No Bridge
source checkout is required. Use the resident files and host from the same
package; a DLL update cannot upgrade an already-running older resident.

The Bridge repository contains Engine documentation and independent package-only
examples. Native attachment and PD GUI acceptance remain separate from managed
package/build evidence.

Earlier 1.11.0/1.9.0 packages and PDFs remain unchanged for provenance. They are
not the current build dependencies.

The package's upstream documentation includes development-machine example paths
and external project links. These are retained as supplied; PD-Simple builds
against this local package and does not require those source checkout paths.
