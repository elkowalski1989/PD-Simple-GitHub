# PD Simple adoption of Allegro Engine

PD Simple is an Engine consumer. It owns engineering policy and product workflow;
it does not own a parallel board model, generic drawing renderer, scene viewer, or
second Bridge connection.

This final local integration pins the exact `1.13.0-preview.8`
SDK/Engine.Core/Engine/WPF package set generated from the final coordinator Bridge
commit. The versioned bundle
manifest and package hashes, rather than this document, establish provenance.

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

## Deliberate low-level SDK seams

`scripts/check-engine-boundary.py` prevents new direct SDK dependencies and lists
the remaining migration seams. They exist for current responsibilities that do
not yet have a qualified Engine replacement:

- `BridgeSession.cs`: application connection owner, activation/lifecycle events,
  and product-level operation coordination;
- `ConnectionSwitchPolicy.cs`: PD's connection-choice and switch policy;
- `SimpleToolExtension.cs`: the product entry extension registered with the SDK;
- `BoardOverlayController.cs`: selected-window binding, native pixel/canvas state,
  and the interactive route HUD around the shared renderer;
- `InteractiveRouteRecovery.cs`: route-specific SDK recovery receipts retained by
  the product workflow;
- `DpViaCorridorNativeCapture.cs`: application-owned Allegro pixel capture used in
  the historical review/export surface;
- `DpViaCorridorBoardOverlay.cs`: exact SDK board/session identity retained beside
  the Engine live-scene fence.

`PD.PcbTools` is Engine-only. `BoardOverlayDrawingPolicy` is no longer an SDK seam.
Any new direct SDK use fails the boundary check unless the allowlist and this
rationale are updated together.

## Verification and remaining native limits

The source-linked build and managed drawing checks prove the typed Workbench host,
canonical drawing intent, live-scene identity fence, and shared renderer
composition. They do not prove live Allegro alignment, native edits, operator
confirmation, screen sharing, or sustained GUI behavior. Run the existing
`Development-Acceptance.md` disposable-board and operator checks after release
integration supplies the matching protected host, resident, and packages.

The package-only product check must consume all three packages at the exact same
version. Do not substitute project references or an older host for that release
gate.
