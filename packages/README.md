# Packaged SDK dependency

Current source requires **1.12.0-preview.2**. Those new runtime packages are not
claimed to be included by this source commit. Build the matching development
bundle with the SDK's `scripts/build-development-bundle.ps1` and copy both exact
packages here as described in the root README. Do not overwrite a different
package with the same version or mix old host/resident files with new assemblies.
The older files listed below remain unchanged for provenance.


Current exact dependencies:

- `CircuitHub.AllegroBridge.Sdk.1.12.0-preview.1.nupkg`
- `CircuitHub.AllegroBridge.Wpf.1.12.0-preview.1.nupkg`
- [Owner handoff and verification](CircuitHub.AllegroBridge.1.12.0-preview.1.HANDOFF.md)

These matching, immutable packages were supplied by the SDK owner. They are local
unsigned development previews, not publicly released SDK packages. Their recorded
repository commit identifies the base checkout; they also contain the owner's
follow-up changes and do not correspond to a clean source tag.

Copied package SHA-256 values:

- SDK: `2aec322c25ff96d4ae4e3a32a8b5fdd8ba1c792e48e08e55be0bb431bbd1f22d`
- WPF: `7b0abb07965fd1555fac261b6b969fb991f06bc967bb752f610d80007eb29343`

`NuGet.Config` resolves the exact versions from this local folder. No Bridge
source checkout is required. Use the resident files and host from the same
package; a DLL update cannot upgrade an already-running older resident.

The package contains SDK documentation and an independent `samples/BoardCatalog`
example. See the handoff for native attachment proof and the remaining geometry,
module and visual-verification limits. PD Simple's own GUI acceptance is separate
from the SDK owner's acceptance.

Earlier 1.11.0/1.9.0 packages and PDFs remain unchanged for provenance. They are
not the current build dependencies.

The package's upstream documentation includes development-machine example paths
and external project links. These are retained as supplied; PD-Simple builds
against this local package and does not require those source checkout paths.
