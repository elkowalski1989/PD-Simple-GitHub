# Engine consumer acceptance

No studio is included. The reference uses normal C#/XAML and public packages.

## Source integration

Build the configured **1.13.0-preview.2** SDK/Engine/WPF bundle in the SDK
repository, then put all three matching packages in `packages/`. Do not overwrite
a previously consumed version with different bytes. The native host/resident and
managed API must come from the same build. Existing license/entitlement and
specific recovery protections remain in force.

Normal builds use the packages. Maintainer-only `AllegroBridgeSourceRoot` mode
compiles source without pretending to supply a matching runtime; Publish/Pack
reject it. Do not remove that guard to obtain a release artifact.

## Explorer journeys

1. Find and attach to an actual supported Bridge instance. Read metadata without
   a copper scan. Search components/nets/pairs/layers. Distinguish unavailable
   families from actually empty collections. Inspect string/disconnected pins
   and declared Xnet membership without inventing pair polarity.
2. Acquire a combined board scene. Include contours only when explicitly needed.
   Missing copper detail stays missing. Scene-local references cannot target a
   different capture, and older pin detail is not merged into current geometry.
3. Save and reopen the scene without Allegro. Inspect objects, pan/zoom, measure,
   select components, preview translation/alignment/distribution/rotation and
   run analysis. Offline data never enables native highlight/edit authority.
4. Select an analysis rectangle and layer. Compare centerline and copper-area
   modes. Missing pin/shape data must be indeterminate, not clear. Counts of
   nets, objects and connected locations remain distinct. The rectangle rule is
   explicitly not the existing differential-pair corridor engineering rule.
5. Inspect a captured net's route graph. Total stored length is not a unique
   end-to-end path or propagation delay. Branches, unavailable connectivity and
   selected graph paths must remain explicit.
6. Capture the connected Allegro application into the composed review tab, then
   share that application-owned view and export PNG. Check declared captured
   viewport and historical annotation provenance. No desktop fallback is used.
7. Change the live document or disconnect. The captured scene remains usable as
   historical data; its native targeting is disabled until explicitly reacquired.
8. Copy an example. Compile it in an ordinary package-only C# project. No private
   SDK types, PD-Simple internals, generated board identifiers or custom SKILL
   should be needed for demonstrated supported operations.

## Native and capture requirements remain open

Repeat the production corridor and route cases in `Development-Acceptance.md`
on the matching final runtime bundle. Keep both intended behavior and deliberate
engineering changes explicit; do not turn a historical approximation into a new
native clearance guarantee.

Exercise actual screen sharing/recording while it remains active: local display,
receiver/video output, zoom/pan, focus, window size, monitor/DPI changes and stop/
restart. An owned-window WPF raster test or recovery only after capture stops does
not establish the requested behavior. Record the exact capture application/mode.
Unknown window occlusion remains conservative rather than being bypassed.

The current ruler/placement interactions operate on captured scenes inside the
application, not as native Allegro input islands. Native component/symbol/padstack
editing and broader constraints/DRC/routing remain separate required workflows.
These gaps must not be labeled complete by the Explorer's controls or test count.
