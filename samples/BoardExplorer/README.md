# Board Explorer and captured-scene tools

An ordinary C#/XAML reference application over the public SDK, Engine and WPF
packages. No studio, visual-programming environment, private host APIs or consumer
SKILL are included. Native highlights/views are explicit; scene analysis,
measurements and placement previews do not mutate the board. Explicit native
Apply and commit-on-drag do modify the connected board; use a disposable design
for candidate acceptance.

Put the matching **1.13.0-preview.7 SDK, Engine and WPF** packages in the controlled
feed, then run:

```powershell
dotnet run --project samples/BoardExplorer/BoardExplorer.csproj -c Release
```

Use Find boards, select the intended live instance and Connect. Read metadata
without a copper scan; inspect a component's string pin IDs and declared Xnet/pair
relationships. Highlight/Zoom perform a fresh named lookup, not validation that a
historical object is unchanged. Copy C# emits the public query with escaped data.

Acquire a combined scene to analyze captured copper. Include contours explicitly
when needed. Missing pin copper or unsupported contour detail remains unavailable,
not empty. Save/Open `.allegroscene` to use the same local data and presentation
without Allegro. Offline scenes never acquire native display or edit authority.

The scene view supplies pan/zoom, local selection, rulers, snapping and annotation
interaction. The placement panel creates origin-based translation, rotation,
alignment or distribution previews. Apply converts a live preview through the SDK's guarded native placement API.
Undo targets only that specific edit. Live move uses real window-region handles;
Live ruler is advisory and never edits the board. Escape cancels a drag.
An unknown native result blocks subsequent edits rather than inviting replay.
These new native workflows still require acceptance on the matching package.
Draw a query rectangle for the selected crossing representation/layer. Net,
object and connected-location counts remain distinct. Coverage warnings must be
resolved before any complete/clear claim; the query is not native DRC or SI signoff.
Stored endpoint-graph lengths are not native electrical connectivity or timing.

The review tab captures only the selected Allegro application and composes the
historical annotations through the shared renderer. It is a single-surface
sharing/export option. It does not certify the live overlay under every recorder.

See [engine acceptance](../../docs/Engine-Acceptance.md) for actual native,
package, GUI and sustained-capture checks still required. The root maintainer-only
source-reference mode compiles the integration but cannot publish a runtime.
