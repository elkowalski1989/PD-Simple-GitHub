# Packaged SDK dependency

This directory includes the supplied SDK and its reference documentation:

- `CircuitHub.AllegroBridge.Sdk.1.9.0.nupkg` — the exact build dependency.
- [CircuitHub.AllegroBridge.Sdk.1.9.0.pdf](CircuitHub.AllegroBridge.Sdk.1.9.0.pdf) —
  the SDK reference supplied with the package.

A Git checkout has the SDK needed to build; a Bridge source checkout is not
required. Both supplied files are retained unchanged.

`NuGet.Config` resolves the exact SDK version from this local feed. Do not mix
its resident scripts with a host executable from another package. Follow the SDK
reference for runtime setup and capability requirements.

The package's upstream documentation includes development-machine example paths
and external project links. These are retained as supplied; PD-Simple builds
against this local package and does not require those source checkout paths.
