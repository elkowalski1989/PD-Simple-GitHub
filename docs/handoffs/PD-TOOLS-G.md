# Lane G (Manufacturing, T12) — PD handoff

## Source

- Baseline: PD `fe26182ebfea8ad0b81ce5a0bce49e280b1198c2` (tools/g worktree, clean; no prior lane-G commits).
- Branch: `tools/g`, head `c29adc3dc622670b028ba9fd85241503dde55ad4` (further doc commit to follow).
- Contracts: frozen `CircuitHub.AllegroBridge.Engine` / `.Wpf` `1.13.0-preview.93` — every Engine member used here was verified present by compiling this branch against the pinned packages.
- Kit refs: TECHNICAL_IMPLEMENTATION_PLAN §16 + §§1–4/18, LANE_G.md, ACCEPTANCE_MATRIX T12-01…T12-06, CONTROL_INVENTORY NAV-T12.

## Owned additions (PD Tools/Manufacturing; no shared-file edits)

- `src/PD.PcbTools/Manufacturing/ManufacturingRunner.cs`
  - `IManufacturingExportRunner`: plan + `ManufacturingRunnerContext` (approved root, Cadence root, timeout, caller-owned config texts) → staged results; staging ownership transfers to the caller.
  - `UnqualifiedManufacturingRunner`: returns the Engine's explicit NoGo admissions (`artwork_native_contract_unqualified`, `odb_output_not_observable`, `ipc2581_native_contract_unqualified`) with null staging. Nothing launches; promotion stays disabled.
- `src/PD.PcbTools/Manufacturing/ManufacturingPageModel.cs`
  - Offline-first: opens `Disconnected`; `SetConnected` + `RefreshSource` turn a refused live capture into `NoCanonicalSource` with the Engine's reason, never an exception.
  - Pure plan builders (Artwork films/options/destination, ODB++ step/layers/options, IPC-2581 layers/`.xml`/revision/units/content/configs) returning displayable errors; expected-manifest previews; run passthrough retaining staging; `CanPromote` (Complete + held staging + not consumed) and approving `Promote`; `ReleaseStaging`; per-format one-line summaries.
- `src/PD.Simple/Manufacturing/ManufacturingView.xaml(.xaml.cs)`
  - Real task page: source fence card, per-format plan cards (film grid lines `name | layers | artifact | polarity | mirror`; all 18 IPC-2581 content flags as generated checkboxes; ODB++ padflash/outline/thermal; caller-owned `art_param.txt`/`art_aper.txt`/property/mapping editors), approved root + relative destination + timeout + replace policy, plan/manifest preview, Generate/Cancel, result text, staged-file promotion preview, Approve-and-promote, Release staging, Save manifest JSON.
  - Every actionable control carries an `AutomationProperties.AutomationId` (`Mfg*`); disabled actions keep a visible reason in `MfgActionReasonText`; keyboard focus is default WPF (no custom tab traps).
  - `Attach(BridgeSession)` borrows the shared session (no second Host, no disposal); `Runner` property defaults to the unqualified runner and accepts the Engine-backed binder at integration with zero view changes.

## Tests (this session, Linux)

- `tests/PD.PcbTools.Checks/ManufacturingPageChecks.cs` hooked into `Program.cs`: disconnected planning refusal, no-acquire-while-disconnected, missing-source status, ready fence, valid/invalid Artwork/ODB++/IPC-2581 plans, manifest previews, all three NoGo runner codes + summaries, promotion refusal for NoGo, null-staging refusal.
- Result: `PD.PcbTools.Checks` PASS 140. `PD.Simple` compiles (EnableWindowsTargeting); WPF interaction itself needs Windows + a live session (coordinator gate).

## Explicit limits / unresolved ops

- Native generation through this page is NoGo until the coordinator binds the qualified Engine exporter (bridge lane-G `ExecuteArtworkAsync`/`ExecuteIpc2581Async` over `ProcessManufacturingNativeLauncher`, next exact preview) and the licensed native qualification passes — see bridge `docs/handoffs/PD-TOOLS-G.md` for the T12 gate table and runbook.
- ODB++ headless execution has no supported native operation in SPB_25.1; the page plans/validates and reports NoGo with evidence.
- IPC-2581 `Complete` additionally needs a supported Cadence compare/import or vendor validator wired to the Engine validator hook.
- Performance: no cold/warm measurements taken here (no native backend in this environment); generation is cancellable with visible progress text.

## Proposed coordinator patch (NOT applied — MainWindow is coordinator-owned)

1. `MainWindow.xaml`: replace line 100
   `<Button Style="{StaticResource FutureNavButton}" Content="Manufacturing  ·  coming"/>`
   with
   `<Button x:Name="ManufacturingMenuButton" Style="{StaticResource NavButton}" Click="Manufacturing_Click"><StackPanel><TextBlock Text="Manufacturing"/><TextBlock Text="BATCH OUTPUT" FontSize="9" Foreground="#6FD6A8"/></StackPanel></Button>`
   and add a hosted `<mfg:ManufacturingView x:Name="ManufacturingView" Visibility="Collapsed"/>` beside `ExplorerView`/`CorridorView` (xmlns `mfg="clr-namespace:PD.Simple.Manufacturing"`).
2. `MainWindow.xaml.cs`: after `_presentation` attach, call `ManufacturingView.Attach(_bridge)`; add `private void Manufacturing_Click(object sender, RoutedEventArgs e) => ShowTool("manufacturing");`; extend `ShowTool` with `ManufacturingView.Visibility = tool == "manufacturing" ? Visible : Collapsed;`; extend `UpdateControls` with `ManufacturingView.IsEnabled = !_bridge.HasRouteInProgress && !_connecting;`; refresh the view on `_bridge.StateChanged` via `ManufacturingView.RefreshFromSession()`; bind `ManufacturingView.Runner` to the Engine-backed runner once the new Engine package lands.
3. No package-pin changes in this lane.

## Session addendum — verification and Engine-backed binder fragment

A parallel Lane G slice landed the implementation above (commits `c29adc3`,
`79d4609`); this session preserved it untouched, re-verified every gate on
the final tree, and adds the binder fragment below. (An intermediate edit
that had replaced this file was reverted via
`git checkout 79d4609 -- docs/handoffs/PD-TOOLS-G.md`; no prior content was
lost.)

- `tests/PD.PcbTools.Checks` PASS 140 (re-run on the final tree; includes
  `ManufacturingPageChecks`). `PD.Simple` view work is compile-covered per
  the prior session (EnableWindowsTargeting); WPF interaction needs Windows
  + a live session (coordinator gate).
- Bridge side re-verified: EngineGate PASS 562, EngineConformanceChecks
  PASS 25. Installed-tree probe (`lane-g-scratch/probe` in Bridge `tools/g`,
  read-only dotnet harness over the real `ManufacturingNativeToolset`):
  `tools/bin/artwork.exe` + `tools/bin/ipc2581_out.exe` present (20,816
  bytes each, same wrapper SHA-256); `odb_out` headless absent (3 searched
  paths missing; only `odbpp_in` import side exists); batch help present
  (`artwork.txt`, `ipc2581_out.txt`). No vendor executable launched.
- `allegro-skill` is not installed in this session's catalog; no SKILL/axl
  surface was needed and no API names were invented.

### Engine-backed runner fragment (coordinator, after the next exact preview)

New file suggestion
`src/PD.PcbTools/Manufacturing/EngineManufacturingExportRunner.cs`,
implementing the existing `IManufacturingExportRunner`. All Engine names are
verified against Bridge `tools/g` (`AllegroWorkspaceManufacturing`,
`ManufacturingNativeExport.cs`, `ManufacturingNativeLaunch.cs`):

```csharp
public sealed class EngineManufacturingExportRunner(AllegroWorkspace workspace)
    : IManufacturingExportRunner
{
    private readonly ProcessManufacturingNativeLauncher _launcher = new();

    public async Task<StagedArtworkResult> RunArtworkAsync(
        ArtworkJobPlan plan, ManufacturingRunnerContext context,
        CancellationToken cancellationToken = default)
    {
        string root = ResolveRoot(context);
        ManufacturingNativeToolPresence tool =
            ManufacturingNativeToolset.RequireArtwork(root);
        string artParam = context.ArtParamText
            ?? throw new InvalidOperationException(
                "Artwork export needs caller-supplied art_param.txt text; the Engine never authors it.");
        var options = new ArtworkExecutionOptions(
            context.Timeout,
            new ArtworkParameterFiles(artParam, context.ArtAperText));
        ArtworkNativeExecution execution = await workspace.Manufacturing
            .ExecuteArtworkAsync(
                plan, context.ApprovedRoot, tool, _launcher, options,
                cancellationToken)
            .ConfigureAwait(false);
        return new(execution.Result, execution.Staging);
    }

    public async Task<StagedIpc2581Result> RunIpc2581Async(
        Ipc2581JobPlan plan, ManufacturingRunnerContext context,
        CancellationToken cancellationToken = default)
    {
        string root = ResolveRoot(context);
        ManufacturingNativeToolPresence tool =
            ManufacturingNativeToolset.RequireIpc2581Out(root);
        var options = new Ipc2581ExecutionOptions(
            context.Timeout,
            new Ipc2581ConfigurationFiles(
                context.IpcPropertyText, context.IpcLayerMappingText));
        Ipc2581NativeExecution execution = await workspace.Manufacturing
            .ExecuteIpc2581Async(
                plan, context.ApprovedRoot, tool, _launcher, options,
                cancellationToken)
            .ConfigureAwait(false);
        return new(execution.Result, execution.Staging);
    }

    public Task<StagedOdbPlusPlusResult> RunOdbPlusPlusAsync(
        OdbPlusPlusJobPlan plan, ManufacturingRunnerContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (OdbPlusPlusOutputResult result, _) = workspace.Manufacturing
            .ReportOdbPlusPlusHeadless(plan, context.CadenceRoot);
        return Task.FromResult(new StagedOdbPlusPlusResult(result));
    }

    private static string ResolveRoot(ManufacturingRunnerContext context) =>
        context.CadenceRoot
        ?? ManufacturingNativeToolset.FindCadenceRootFromEnvironment()
        ?? throw new InvalidOperationException(
            "No Cadence root: set CDSROOT/CDS_ROOT or choose an approved install directory.");
}
```

Notes: borrow the application's shared Engine session (never a second Host).
A non-positive/unbounded `context.Timeout` surfaces as
`ArgumentOutOfRangeException` from the Engine boundary — convert to an
action-level availability reason in the view. No `SupportedValidator` is
wired yet, so IPC-2581 stays `ValidationFailed` until a supported Cadence
compare/import or vendor validator qualifies (Bridge open item Q-IPC-NAME
covers the `-o` naming assumption). The view needs zero changes: its
`Runner` property accepts this binder at integration.
