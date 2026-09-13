# Via Proximity Explorer

This sample has a cross-platform headless Engine entry point and an optional
Windows WPF presentation entry point. Headless analysis supports saved
`.allegroscene` input, an explicit launch context, or one explicitly selected
opaque running-instance target. Both live paths use `AllegroEngineDiscovery`,
one owned `AllegroEngineSession`, and an explicit `Workspace.ReadAsync` call;
the local proximity query never performs acquisition.

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

`ViaProximityExplorer.Presentation` accepts the same analysis options but
requires `--bridge-dir` or `--running-target`. It creates one Engine session
requiring scene-read and presentation capabilities, acquires and analyzes
through that session, and passes the resulting `LiveDesignScene` and standard
`AnnotationScene` directly to `EngineWpfPresentation.PresentAsync`. The WPF
facade is attached to that exact session; it does not discover or open another
connection. Ctrl+C calls `Clear`, disposes WPF presentation, and only then
disposes the Engine session. Saved scenes remain headless evidence and are never
promoted into live presentation authority.

The query produces annotations, so this sample uses the facade's annotation
overload. It does not translate findings into a `DrawingScene`, raw pixels, or a
native overlay adapter merely to exercise another overload.

Exit code `0` means the query collection is complete for its declared scope,
`3` means it is partial or indeterminate, `2` means canceled, `1` means the
operation failed, and `64` means the arguments are invalid. A measured result is
an application question over captured data, not a native signoff. Missing or
unsupported geometry never means clear space.

This sample does not change PD production composition. PD Lane 05 owns the one
application-lifetime Engine session, every `BridgeSession*.cs` file, and the
composition root. PD Lane 06 owns corridor view/presentation policy and borrows
the session plus `EngineWpfPresentation`; it neither creates nor disposes the
Engine session. Lane 07 owns only this isolated example.

Session 01 owns persistent sample project and solution wiring. The headless and
WPF entry points remain separate so saved-scene analysis stays cross-platform.
Until that wiring is integrated, maintainers can compile the two source sets
against exact `PRESENTATION_API_READY` Bridge checkpoint
`d6eac3151965c8c2dec9655d93d62ec535b1b94d` with temporary projects.
