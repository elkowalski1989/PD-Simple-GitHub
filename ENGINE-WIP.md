# Engine consumer implementation

The previous checkpoint retained the earlier PD-Simple implementation while
newer uncommitted integration was unavailable. This branch now implements a new
public-API Engine integration, rather than claiming recovery of lost files.

Board Explorer can read lightweight metadata, acquire a combined scene, open and
save data-only captures, inspect relationships and route graphs, use the WPF
scene viewer/ruler/placement-preview controls, run an explicit rectangle-crossing
analysis, and compose/export a historical review image. These are reference
consumer tools, not a studio or authoring IDE. C# recipes use public package APIs.

The production PD-Simple capture now uses the SDK capture owner while retaining
its exact serialized viewport and native document checks. Its owned overlay
shares bounded compositor recovery, without replaying operations or weakening
native identity, clipping, readback or edit-specific recovery.

The candidate targets matching SDK/Engine/WPF **1.13.0-preview.3** packages. Source
build mode remains explicitly compiler-only. The matching runtime bundle must be
built with the SDK's normal configured inputs before native testing.

Native acceptance, sustained recorder/sharing behavior and the wider symbol,
padstack, constraint, editing and routing roadmap remain open as documented in
`docs/Engine-Acceptance.md` and the SDK's `docs/Engine-Implementation.md`.
