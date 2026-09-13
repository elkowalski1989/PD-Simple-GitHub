# Via Proximity Explorer

This headless sample composes the public `CircuitHub.AllegroBridge.Engine` API.
It supports saved `.allegroscene` input, an explicit launch context, or one
explicitly selected opaque running-instance target. The live paths use
`AllegroEngineDiscovery`, one owned `AllegroEngineSession`, and an explicit
`Workspace.ReadAsync` call; the local proximity query never performs acquisition.

Choose a source net or exact selected-via scene object IDs, target nets or exact
selected-shape IDs, a threshold, center-distance or copper-gap measurement, and
shared-layer or opt-in planar cross-layer policy. Layer filters, equality,
grouping, ties, and resource budgets are also explicit. The JSON report retains
the Engine query requirements, evidence-qualified results, coverage/provenance,
and standard design-space annotations.

Use `--list-running` before `--running-target` when more than one Allegro instance
may be available. Discovery never auto-selects a target. `--bridge-dir` resolves a
launch context through Engine; the sample does not interpret that path as a
runtime session or contact a lower SDK directly.

Exit code `0` means the query collection is complete for its declared scope,
`3` means it is partial or indeterminate, `2` means canceled, `1` means the
operation failed, and `64` means the arguments are invalid. A measured result is
an application question over captured data, not a native signoff. Missing or
unsupported geometry never means clear space.

Session 01 owns the persistent sample project and solution wiring. Until that
wiring is integrated, maintainers can compile these source files against the
exact `LIVE_API_READY` Engine source checkpoint with a temporary project.
