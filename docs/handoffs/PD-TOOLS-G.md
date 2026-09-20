# Lane G (Manufacturing, T12) — PD handoff

## Source

- Baseline: PD `fe26182ebfea8ad0b81ce5a0bce49e280b1198c2`, branch `tools/g`.
- This session made no PD code edits: the PD seam below already exists at
  baseline and compiles green against the pinned Engine
  `1.13.0-preview.93`. The Engine execution API it must bind to is new on
  Bridge `tools/g` (see the Engine handoff in the Bridge repo,
  `docs/handoffs/PD-TOOLS-G.md`) and ships with the next exact preview.
- Kit refs: TECHNICAL_IMPLEMENTATION_PLAN §16, ACCEPTANCE_MATRIX T12-01…T12-06,
  CONTROL_INVENTORY NAV-T12, AUDIT S15/S16.

## PD seam (existing, baseline)

- `src/PD.PcbTools/Manufacturing/ManufacturingPageModel.cs` — offline-capable
  page model. Opens `Disconnected` with setup guidance; `RefreshSource`
  fences via the live workspace capture and reports `NoCanonicalSource`
  instead of throwing. Pure plan builders (`TryBuildArtwork`,
  `TryBuildOdbPlusPlus`, `TryBuildIpc2581`) return displayable errors for
  filmless films and duplicates. Manifest previews before any run
  (`PreviewArtworkManifest`, `PreviewOdbManifest`, `PreviewIpcManifest`).
  `RunArtworkAsync` / `RunOdbPlusPlusAsync` / `RunIpc2581Async` delegate to
  the injected `IManufacturingExportRunner` and retain staging for approval.
  `CanPromote` admits only `Complete` + held staging + unpromoted job;
  `Promote` commits then releases; `ReleaseStaging` discards. Summarizers
  for all three formats.
- `src/PD.PcbTools/Manufacturing/ManufacturingRunner.cs` —
  `IManufacturingExportRunner`, `ManufacturingRunnerContext` (approved root,
  Cadence root, timeout, caller-owned config texts), staged-result records,
  and `UnqualifiedManufacturingRunner`, which reports the Engine's explicit
  NoGo admission per format, launches nothing, and keeps promotion disabled.
- `src/PD.Simple/MainWindow.xaml` line 100 — `Manufacturing · coming`
  `FutureNavButton` (NAV-T12, permanent placeholder). Untouched: coordinator
  owns MainWindow and the tool registry.

## Gates

- `tests/PD.PcbTools.Checks` PASS 140 (includes `ManufacturingPageChecks`:
  disconnected open, no plan without fence, no acquire while disconnected,
  missing canonical source, ready fence, valid/duplicate/filmless Artwork
  plans, ODB++ plan + empty-layers refusal, IPC-2581 plan + manifest,
  honest NoGo from the default runner for all three formats, promotion
  refused for NoGo and without staging). No Allegro or GUI execution.
- Bridge side re-verified this session: EngineGate PASS 562,
  EngineConformanceChecks PASS 25. Installed-tree probe evidence (artwork +
  ipc2581_out present, odb_out headless absent, batch-help inventory) is in
  the Bridge handoff.

## Integration need 1 — Engine-backed runner (coordinator, after new preview)

New file suggestion
`src/PD.PcbTools/Manufacturing/EngineManufacturingExportRunner.cs`,
implementing the existing `IManufacturingExportRunner` over the qualified
Bridge API. All Engine names below are verified against Bridge `tools/g`
(signatures in `AllegroWorkspaceManufacturing`,
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

Notes: the workspace is the application's shared Engine session (borrowed,
never a second Host). A non-positive/unbounded `context.Timeout` surfaces as
`ArgumentOutOfRangeException` from the Engine boundary — convert to an
action-level availability reason in the page. No `SupportedValidator` is
wired yet: IPC-2581 results stay `ValidationFailed` until a supported
Cadence compare/import or vendor validator qualifies (Bridge open item
Q-IPC-NAME covers the `-o` naming assumption).

## Integration need 2 — navigation registration (coordinator owns)

Replace the NAV-T12 `FutureNavButton` with the registered Manufacturing tool
entry resolving to exactly one tool. The page opens while disconnected
(`ManufacturingPageModel` starts `Disconnected`); only actions gate:
`CanPlan` (source fence), runner NoGo states, `CanPromote` (Complete +
staging). Show each reason beside the action with a corrective step. No
package-pin or central-registry edits were made here.

## Limits and unresolved operations

- T12-01/T12-03 final native pass: BLOCKED on a licensed Windows run of the
  exact adapters against a disposable board (runbook in the Bridge handoff).
  No mock substituted; feasibility stays NoGo.
- T12-02 (ODB++): no headless launcher in SPB_25.1 — unresolved vendor
  requirement. The page still plans ODB++ output; execution reports the
  searched absence. Scope not reduced silently.
- `allegro-skill` is not installed in this session's catalog; no SKILL/axl
  surface was needed and no API names were invented.
