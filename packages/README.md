# Packaged Engine dependency

Current source consumes the exact **1.13.0-preview.26** Engine generation in this
directory. `development-bundle.1.13.0-preview.26.json` is the authoritative
four-package identity and hash record. Ordinary PD projects directly reference
Engine and optional WPF; SDK/Host/resident assets remain transitive implementation
details of that same generation.

Current exact packages:

- `CircuitHub.AllegroBridge.Engine.Core.1.13.0-preview.26.nupkg`
- `CircuitHub.AllegroBridge.Engine.1.13.0-preview.26.nupkg`
- `CircuitHub.AllegroBridge.Wpf.1.13.0-preview.26.nupkg`
- `CircuitHub.AllegroBridge.Sdk.1.13.0-preview.26.nupkg` (transitive runtime owner)

These immutable files came from clean Bridge source commit
`da821e81a7deb10ed8ddc2527d92bac805b7790d`. They are an unsigned local
development candidate, not a public release or a claim that the broader P0
program is complete. The retained Preview.26 qualification establishes its
recorded targeted native/WPF scope while leaving the disclosed performance,
fault-injection, physical-display, and partial-native gates open. Do not rebuild
or overwrite Preview.26 with different bytes; changed bytes require a new version.
Do not mix older Host/resident files with these assemblies.

`NuGet.Config` resolves the exact versions from this local folder. No Bridge
source checkout is required. Use the resident files and host from the same
package; a DLL update cannot upgrade an already-running older resident.

The Bridge repository contains Engine documentation and independent package-only
examples. Native attachment and PD GUI acceptance remain separate from managed
package/build evidence.

Earlier Preview.16, 1.11.0, and 1.9.0 packages and PDFs remain unchanged for
provenance. They are not the current build dependencies.

The package's upstream documentation includes development-machine example paths
and external project links. These are retained as supplied; PD-Simple builds
against this local package and does not require those source checkout paths.
