# PD Simple adoption of Allegro Engine

PD Simple is an Engine consumer. It owns engineering policy and product workflow;
it does not own a parallel board model, generic drawing renderer, scene viewer, or
second Bridge connection.

This checkout consumes the SDK/Engine.Core/Engine/WPF package set pinned by
`AllegroBridgePackageVersion` in `Directory.Build.props`. (Historical note: the
original integration record below was written against `1.13.0-preview.26` from
Bridge source commit `da821e81a7deb10ed8ddc2527d92bac805b7790d`.) The versioned
bundle manifest and package hashes, rather than this document, establish
provenance.

## Runtime ownership

`BridgeSession` owns the one Engine session chosen by PD Simple. Engine owns its
lower SDK connection and exposes one stable `AllegroWorkspace`; PD provides the
live Engine scene and application-owned review capture to `EngineExplorerView`.

`EngineExplorerView` hosts `EngineWorkbenchView` through its public typed API. It
does not use `Type.GetType`, reflection, a fallback UI, or hidden board discovery.
The Workbench never creates or replaces an SDK session. If live acquisition is
unavailable, its previously loaded offline scene remains useful for local
selection, inspection, drawing, and analysis.

The host forwards these lifecycle decisions to PD Simple:

- `IsBusy` blocks conflicting product commands;
- `CanClose` blocks shutdown while a dispatched native edit is still being tracked;
- `CanSwitchNativeSession` blocks connection replacement for both in-flight and
  unresolved native outcomes;
- `NativeSessionRetentionReason` explains the current boundary to the user.

Changing tabs, opening another product surface, refreshing a capture, or navigating
inside the Workbench does not clear an unresolved edit. PD Simple does not replay
an operation after cancellation or a connection change.

## Drawing and overlay ownership

The DP via-corridor rule remains PD policy:

- `PD.PcbTools` owns pairing, corridor dimensions, layer/pad assumptions, and
  screening diagnostics;
- `BoardOverlayDrawingPolicy` converts a finding into canonical Engine
  `DrawingGroup` intent, including the P/N axis, measured corridor, labels, and
  optional intrusion marker;
- Engine owns physical units, immutable scene binding, and drawing projection;
- shared Bridge WPF owns live viewport admission, clipping, rasterization, and the
  nonactivating overlay window;
- `BoardOverlayController` owns only product orchestration and its separate
  interactive route-picking HUD.

The live corridor overlay carries the exact `LiveDesignScene`, document identity,
and selection revision that produced it. A session, board, design, revision, or
freshness mismatch removes the overlay instead of projecting stale geometry.

The captured corridor review/export surface in `DpViaCorridorCanvas` still owns
PD-specific presentation of the native screenshot and explanatory annotations.
It is intentionally retained because it combines an application-owned historical
image with the corridor report; it is not used for the live overlay. This is the
remaining corridor drawing duplication to consider after the shared review
adapter supports that product composition directly.

## Platform and product boundary

| Owner | Responsibilities |
| --- | --- |
| Allegro Bridge SDK | Connection, authorization, native dispatch, document/session fencing, canvas observations. |
| Allegro Engine | Immutable design scenes, geometry, relationships, selection, drawing intent, tool lifecycle, guarded edit/constraint/routing/manufacturing contracts. |
| Allegro Bridge WPF | Workbench, scene viewer, drawing projection, live overlay, review composition, WPF lifetime handling. |
| `PD.PcbTools` | Differential-pair corridor engineering policy and horizontal-first route policy. |
| `PD.Simple` | Menus, workflow sequencing, confirmation, reports, connection chooser, product status, route-picking HUD. |

PD Simple exposes only operations backed by current capabilities and complete
evidence. The Engine Workbench's editing and routing surfaces remain disabled
unless a live workspace and their exact native capabilities are available.
Captured DRC, constraint, symbol, and padstack data can be inspected when present;
missing families remain explicit. Manufacturing execution is not exposed because
the Engine baseline deliberately marks native generation unqualified.

## Zero ordinary lower-SDK seams

`scripts/check-engine-boundary.py` rejects direct lower-layer namespaces,
packages, or assembly references throughout maintained production, test, and
sample code. There is no ordinary-source allowlist. `PD.PcbTools`, `PD.Simple`,
and every maintained sample consume Engine and optional WPF APIs; the lower SDK,
Host, Windows binding, protocol, and transport remain Engine implementation
details delivered transitively by the matching package generation.

The noncompiled `SimpleToolExtension.cs` and `InteractiveRouteRecovery.cs`
migration remnants were removed after confirming their exact contents remain in
Git history at base `d501212805384400c17bfe220be46a05be172a55`. The excluded
lower-SDK Board Explorer `MainWindow*` implementation was removed on the same
basis. No compiled behavior depended on those files. Native comparison material
under `src/PD.Simple/Skill` remains source-only and is not an ordinary C# seam.

`BridgeSession` is now the product composition owner over one
`AllegroEngineSession`; it does not own a second transport. Connection selection,
native admission, capability truth, uncertain/recovery state, and lower cleanup
remain Engine-owned. Any new direct lower-SDK source or project dependency fails
the boundary check rather than being added to a migration allowlist.

## Verification and remaining native limits

The emitted-assembly boundary check, package-only build, and managed drawing
checks prove the typed Workbench host, canonical drawing intent, live-scene
identity fence, and shared renderer composition. The matching Preview.26 package
passed its retained targeted Allegro 25.1 native/WPF regression, including public
drawing, complete live Engine capture, composition, and focused WPF performance.
That bounded result does not close the broader Preview.25 matrix, every PD
corridor case, operator confirmation, screen-sharing mode, sustained GUI behavior,
or the retained performance and fault-injection gates. Use
`Development-Acceptance.md` for those product-specific checks.

The package-only product check must consume all four packages at the exact same
version. Do not substitute project references or an older host for that release
gate.
