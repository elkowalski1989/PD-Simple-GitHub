# Board Explorer and captured-scene tools

An ordinary C#/XAML reference application over the public Engine and WPF
packages. `EngineFirstWindow` owns exactly one `AllegroEngineSession`, attaches
`EngineWpfPresentation` to that session, and gives the same presentation to
`EngineWorkbenchView`. WPF does not discover, connect, switch, or dispose the
Engine session.

Select the exact matching Engine and WPF candidate from a controlled feed, then
run:

```powershell
dotnet run --project samples/BoardExplorer/BoardExplorer.csproj -c Release `
  -p:AllegroBridgePackageVersion=<exact-version>
```

Use Find boards, deliberately select the intended Engine target, and Connect.
Engine revalidates that selection on attach. The Workbench owns explicit scene
acquisition, typed inspection, captured-scene analysis, semantic display
operations, guarded native edits, review capture, and export.

Acquire a combined scene to analyze captured copper. Include contours explicitly
when needed. Missing pin copper or unsupported contour detail remains unavailable,
not empty. Save/Open `.allegroscene` to use the same local data and presentation
without Allegro. Offline scenes never acquire native display or edit authority.

The scene view supplies pan/zoom, local selection, rulers, snapping, canonical
annotations, and canonical `DrawingScene` interaction. Workbench routes supported
live actions through Engine authorities and retains uncertain or recovery-relevant
outcomes rather than inviting replay. Native workflows still require acceptance
on the matching package and a disposable board.
Draw a query rectangle for the selected crossing representation/layer. Net,
object and connected-location counts remain distinct. Coverage warnings must be
resolved before any complete/clear claim; the query is not native DRC or SI signoff.
Stored endpoint-graph lengths are not native electrical connectivity or timing.

The review tab calls the same-session `EngineWpfPresentation` facade. Its composed
image is historical after capture and does not certify the live overlay under
every recorder.

The project has no project reference, linked source, source-root override,
direct SDK/Windows reference, native adapter, or default package version. The
legacy `MainWindow*` source is retained but excluded from the ordinary project as
explicit advanced SDK reference material; it is not compiled or runnable from
this package consumer.

PD05 continues to own the PD product connection/composition root, and PD06
continues to own PD product presentation. This sample-only migration does not
edit, replace, or claim either product boundary.

See [engine acceptance](../../docs/Engine-Acceptance.md) for actual native,
package, GUI and sustained-capture checks still required.
