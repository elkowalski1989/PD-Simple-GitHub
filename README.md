# PD-Simple

A standalone C# example of using **Allegro Bridge SDK and WPF 1.12.0-preview.1** with two tools:

- **DP via corridor** — setup, findings, native bitmap preview, measured
  highlights, Allegro navigation, filtering, report, and raw/annotated PNG export.
- **Point-to-point trace** — choose a width, pick two objects in Allegro, and
  create one H-first route. Includes native pick feedback, Cancel, Clear first
  pick, and guarded Undo.

Only the packaged Bridge SDK is referenced; neither the full PD Workflow Engine
nor a Bridge source checkout is required. This is a working two-tool reference,
not a minimal hello-world sample. The SDK owns integration with Allegro; the
application retains the native checker and routing algorithms. Start with
[Where to read the example](#where-to-read-the-example) for the reusable SDK flow
and [SDK versus application ownership](#sdk-versus-application-ownership) for
the boundary between integration and board-specific operations.

## Install and use

1. Extract **PD-Simple-Setup.zip**, then run `PD-Simple/Install.cmd`.
2. Restart Allegro and open a board.
3. Enter **`pd_simple`** in Allegro's command line. Choose one of the two tools.

Installation is per-user at `%LOCALAPPDATA%\PD-Simple`. It adds one managed load
block to `%APPDATA%\SPB_Data\pcbenv\allegro.ilinit`. It does **not** edit Cadence
system menus, remove the full Workflow Engine, or auto-open another window on
every board load. You can assign `pd_simple` to your own Allegro shortcut/menu.

One Bridge companion owns an Allegro session at a time. If the full Workflow
Engine is already connected, `pd_simple` asks before switching companions and
refuses to switch during a pending native operation. It does not silently restart
another app. Original boards are not saved by the launcher or the checker.

**Reconnect** opens a chooser for running Allegro instances. Select a board and
choose **Attach selected**, or use **Reconnect current** to refresh the existing
connection. The chooser also works when PD Simple is started directly, without
launch arguments. Cancel or a failed attachment retains the current connection.

The target needs the matching preview Bridge resident and this build's PD Simple
tools already loaded; start it with `pd_simple` first. Older or unavailable
residents are listed with a reason. Attachment proves the selected native session
and binds the exact tool content; it never loads/replaces tools or closes another
companion or Allegro. It does not open an unopened `.brd` file: open that file in
Allegro first. No window-title or session-directory guessing is used.

Catalog changes rebind the already accepted tool identity while idle, without
reloading SKILL or replaying operations. Active operations prevent connection
changes. Uncertain-outcome and guarded-recovery evidence survives reconnect;
resolve it before switching boards. Old board findings become historical after
switching and cannot navigate or draw on the new board.

The installer verifies payload hashes before installing and retains an earlier
installation plus a startup-file backup. To remove only PD Simple, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\PD-Simple\Install.ps1" -Uninstall
```

Removal preserves other startup entries and moves the installed directory to a
recoverable `.removed-...` sibling. The current SKILL launcher requires an ASCII
installation path; it refuses unsupported paths instead of silently corrupting
them. There are no admin or certificate-installation steps in this example.

## Build and share the source

Requirements: Windows x64, .NET 10 SDK, Allegro with SKILL support, and the Bridge
SDK's applicable entitlement and publisher-trust requirements. The GUI is
self-contained when published, so teammates running the setup do not need the
.NET SDK. Unsigned consumer SKILL still needs the SDK's online
`bridge.custom.execute` capability. See the supplied SDK reference for setup.

The exact `CircuitHub.AllegroBridge.Sdk.1.12.0-preview.1.nupkg` and matching WPF
package are included in `packages/`, alongside the
[preview handoff](packages/CircuitHub.AllegroBridge.1.12.0-preview.1.HANDOFF.md).
This is an unsigned development preview, not a production SDK release.
See [packages/README.md](packages/README.md). `NuGet.Config` maps this
dependency to that directory; do not substitute a host or resident from another
package. Normal .NET restore may contact nuget.org for framework/runtime packs.

Run **`Build.cmd`**, or:

```powershell
dotnet build src/PD.Simple/PD.Simple.csproj -c Release
./Build.ps1
```

Build produces `artifacts/PD-Simple-Setup.zip` and
`artifacts/PD-Simple-Source.zip`. The source archive includes this project and
the supplied SDK package and PDF. They exclude local boards, logs, credentials,
and dependency caches. The unpacked build is printed at the end.
Start the application through its Allegro launcher, or launch its executable and
use Reconnect to explicitly choose an available board.

## Repository checks

From the repository root on Windows, with the included SDK package in place:

```powershell
dotnet build src/PD.Simple/PD.Simple.csproj -c Release
dotnet run --project tests/PD.Simple.Checks/PD.Simple.Checks.csproj -c Release
dotnet run --project tests/PD.Simple.DrawingChecks/PD.Simple.DrawingChecks.csproj -c Release
```

The checks cover route-result/recovery admission and physical-pixel clipping.
They do not replace live Allegro verification of picking, routing, Undo, capture,
or window behavior. See [CONTRIBUTING.md](CONTRIBUTING.md) for the native check
boundaries.

## Publish to IBM GitHub

Create an empty repository named `PD-Simple` at `github.ibm.com` in your account or
organization. Leave the remote empty so the prepared folder supplies its initial
contents.

The prepared folder already has a local Git repository on `main`, with no
commits or remote. From that folder, substitute your account or organization for
`OWNER`, then review and publish:

```powershell
git add .
git diff --cached --stat
git commit -m "Initial PD-Simple SDK example"
git remote add origin https://github.ibm.com/OWNER/PD-Simple.git
git push -u origin main
```

If you prefer to clone the empty remote first, copy this folder's contents,
including `.gitignore` and `.gitattributes`, into that clone **except for `.git`**.
Preserve the clone's own `.git`, skip `git remote add`, and do not nest a second
project directory inside it. The commands above are for the repository owner's
initial publication, not steps required for teammates who clone it later.

Source, documentation, repository configuration, and the supplied SDK NuGet
package belong in Git. Local boards, test-session files, builds, archives, IDE
state, credentials, and dependency caches are ignored. Nothing is published by
the build scripts.

## Small package-only examples

- [ReadPcb](samples/ReadPcb/README.md) reads bounded geometry for explicit nets and
  reports truncation and unavailable data. It makes no clearance claim.
- [PickAndMeasure](samples/PickAndMeasure/README.md) measures two actual clicked
  positions using the SDK picker, with native Clear/Cancel and no copper edits.

Neither example references PD-Simple internals or supplies consumer SKILL.
The complete two-tool application remains the advanced reference. Its native
checker/router are retained while the full high-level migration is in progress;
see [integration requirements and evidence](CONTRIBUTING.md#high-level-api-integration-in-progress).

## Where to read the example

| File | Responsibility |
| --- | --- |
| `src/PD.Simple/PD.Simple.csproj` | Matching pinned SDK/WPF packages; runtime and SKILL asset copying |
| `src/PD.Simple/BridgeSession.cs` | Connect, load and bind exact commands, execute, await terminal receipts, own lifecycle |
| `src/PD.Simple/MainWindow.xaml(.cs)` | Only the two-tool menu and point-to-point controls |
| `src/PD.Simple/Corridor/` | Retained DP corridor UI, strict result contracts, image capture and annotations |
| `src/PD.Simple/BoardOverlayController.cs` | SDK-owned canvas projection and presentation of consumer drawing primitives |
| `src/PD.Simple/BoardOverlayDrawingPolicy.cs` and `BoardOverlayHud.cs` | Tool styles and HUD; package WPF frame owns board rasterization and SDK geometry owns the clip union |
| `src/PD.Simple/Skill/pd_simple_controls.il` | Consumer extension registration, typed arguments/results, deferred route callbacks |
| `src/PD.Simple/Skill/pd_simple_route_adapter.il` | Small adapter to the maintained point-to-point owner |
| `installer/pd_simple_loader.il.in` | Register `pd_simple`, configure the packaged resident, launch this companion |

The central SDK flow is shown below in abbreviated form; the concrete source,
command IDs, readiness requirements, disposal, and error handling are in
`BridgeSession.ConnectAsync` and its operation methods:

```csharp
var session = await AllegroBridgeSession.ConnectAndWaitUntilReadyAsync(
    new AllegroBridgeConnectOptions(bridgeDirectory), readiness, cancellationToken);
var tools = await session.SkillExtensions.LoadAndBindAsync(
    source, requiredCommandIds, cancellationToken);
var result = await tools.ExecuteToTerminalAsync(commandId, arguments, cancellationToken);
```

Interactive routing uses `ExecuteWithHandleAsync` instead: it keeps the handle,
consumes native interaction feedback, supports cooperative cancellation and
clear-first-pick, and waits for the terminal result. Queue acceptance is not
completion. An expired session/catalog binding is not silently rebound or
replayed. A timeout does not prove native cancellation or Undo.

## SDK versus application ownership

| Owner | Responsibilities |
| --- | --- |
| Packaged Bridge SDK | Host/resident transport, entitlement, session and board identity, extension loading and binding, typed dispatch, deferred-operation protocol, native window binding, capture/projection and overlay infrastructure |
| C# application | Two-tool UI, parameters, result validation, report/export presentation, drawing primitives, and decisions about when to enable retry or Undo |
| Consumer SKILL adapters | Register commands through `pdceRegisterCommand`, read typed arguments, call domain operations, publish typed results and deferred feedback |
| Native domain SKILL | Read Allegro database objects, identify corridor crossings, collect real endpoint picks, create the route transactionally, verify readback, and perform guarded recovery/Undo |

There are approximately 7,100 lines of consumer SKILL in this reference, including
comments and native helpers. Most implement the corridor checker and
router, not a second bridge. The SDK does not provide either tool's domain
algorithm. Moving those operations into C# is a separate redesign, not a package
configuration change.

For a new SDK tool, start with the pinned package reference, the connection and
binding flow in `BridgeSession.cs`, and one command registration/handler in
`pd_simple_controls.il`. Supply only the native operation your tool needs; do not
copy the corridor checker, categories, or router
unless your tool actually needs them. Keep the packaged resident unchanged.
The checker exposes only its SDK adapter: obsolete standalone commands, native
forms, palette/marker visualization, and selection-based analysis branches have
been removed. The WPF application owns presentation; native code retains
analysis, report output, validated navigation, and routing.

## Board behavior and provenance

DPVC analysis is report-only. Selecting a crossing changes Allegro's view, not
board geometry. Raw PNGs preserve owned Allegro pixels; annotated PNGs add C#
marks aligned to verified native viewport bounds. Risk labels are review aids,
not SI sign-off. Old-board results remain explicitly historical.

Canvas highlights use SDK 1.9's focus-independent drawing view: the visible part
of Allegro can stay highlighted while PD Simple is active. The owned overlay is
nonactivating, click-through and non-topmost. Every displayed frame is rechecked;
covered regions are clipped, and unavailable or expired views are cleared. Native
view evidence has a 250-ms maximum age. Pointer selection retains the SDK's
foreground requirement; drawing does not grant picking or mutation authority.

Routing uses the two **clicked positions**, not automatically object centers.
Make the intended ETCH layer visible in Allegro before starting. The maintained
router uses a visible active ETCH layer, falling back to the first visible ETCH
layer. When every ETCH layer is hidden, PD Simple rejects the request before
entering pick mode; make a layer visible and retry. It does not silently change
layer visibility. Later native failures remain locked until their outcome is
proven safe.
Same-net or one-known-net endpoints produce a net cline; conflicting nets or no
derived net produce unassigned etch. It is not clearance-aware autorouting.
Inspect the result and run the normal DRC. The native owner performs transaction,
readback, and recovery checks. Selection is part of the routing interaction and
is not promised to preserve an arbitrary earlier selection.

The corridor view/capture/geometry, route feedback, native checker, categories,
and point-to-point implementation were carried over from PD Workflow Engine on
2026-09-09. The original project is not modified or referenced. Only surrounding
catalog and component-pattern dependencies were removed; the corridor's back
button now returns to the two-tool menu. Native globals are intentionally retained
with the maintained owners, which is another reason not to load both companions'
extensions concurrently in the same native session.
