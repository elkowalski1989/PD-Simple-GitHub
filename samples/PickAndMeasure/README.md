# Pick and measure through Engine

A package-only .NET 10 console example that directly references only
`CircuitHub.AllegroBridge.Engine`. It measures two clicked positions without
creating traces or changing board geometry. It has no PD Simple dependency,
project reference, linked source, source-root override, or consumer SKILL.

Build against the coordinator's exact package candidate:

```powershell
dotnet build samples/PickAndMeasure/PickAndMeasure.csproj `
  -p:AllegroBridgePackageVersion='<exact-version>'
```

Run in an interactive Windows console with Allegro open and the matching Bridge
resident already active:

```powershell
dotnet run --project samples/PickAndMeasure `
  -p:AllegroBridgePackageVersion='<exact-version>' -- `
  --bridge-dir "C:\path\to\active-bridge"
```

Engine resolves the explicit target, owns the connection, checks the picking and
routing capabilities, and starts `workspace.Picking.StartTwoPointPickAsync`.
Native hit-testing supports the object kinds projected by
`EnginePickedObjectKind`; returned positions are accepted click observations,
not inferred object or pin centers.

In the console:

- Press **C** to request `ClearFirstAsync`.
- Press **X** or **Ctrl+C** to call `EngineEndpointPick.CancelAsync` and keep
  waiting for the Engine terminal result.
- Ctrl+C during connection/setup cancels that setup wait. If it arrives while
  the pick handle is being acquired, the queued request targets that handle.

Selection feedback is optional and uses `EngineInteractionFeedback`. A local
feedback/control-reader error does not replace the independently awaited terminal
result. After a complete result, the sample calculates Euclidean distance from
canonical decimal-mil `DesignPoint` values and reports retained native units and
precision. This is neither routed length nor copper clearance.

Disposal or a canceled managed setup wait is not native cancellation evidence.
If Engine reports an unresolved operation, inspect session state and Allegro
instead of retrying. Exit codes: `0` measured result, `1` failure, `2` confirmed
native cancellation or interrupted setup, `64` invalid arguments.
