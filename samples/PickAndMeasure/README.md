# Pick and measure

A package-only .NET 10 console example using `CircuitHub.AllegroBridge.Sdk` **1.12.0-preview.1**. It measures two clicked positions without creating traces or changing board geometry. It has no PD Simple dependency, project references or consumer SKILL.

Build from this repository root:

```powershell
dotnet build samples/PickAndMeasure/PickAndMeasure.csproj
```

Run in an interactive Windows console with Allegro open and an existing bridge session. Substitute its actual bridge directory:

```powershell
dotnet run --project samples/PickAndMeasure -- --bridge-dir "C:\path\to\active-bridge"
```

The repository's `NuGet.Config` resolves the exact SDK from `packages/`; copying this example elsewhere also requires a feed containing that release package. This sample does not launch Allegro, install certificates or create a bridge session directory.

The sample prints the operation ID before waiting. In Allegro, pick two different supported PCB objects. Native hit-testing supports components, symbols, nets, pins, vias, clines, cline segments and lines. The returned coordinates are the accepted clicked positions, **not automatically object or pin centers**.

In the console:

- Press **C** to request `ClearFirstAsync`. The SDK/native operation rejects it if there is no first pick to clear.
- Press **X** or **Ctrl+C** to call the current operation's `CancelAsync`. The sample continues waiting for the native terminal receipt. A failed/timed-out cancel request is not confirmation of cancellation.
- Ctrl+C during connection/setup cancels that setup wait. If it arrives while the picker handle is being acquired, the queued request targets that handle once available.

Selection feedback is optional presentation. A feedback-reader error does not discard the independently awaited endpoint result. The console prints the original receipt and typed endpoints, then calculates Euclidean distance in mils and reports the native units/decimal precision. This is neither routed length nor copper clearance.

The native picker temporarily manages input and restores selection/Find filters. No database transaction spans human input, and this example never calls trace creation or Undo. Disposing an operation or stopping a managed wait must not be described as native cancellation. If communication fails before a terminal receipt, inspect the printed operation and Allegro instead of assuming the native picker stopped.

Exit codes: `0` measured result, `1` failure, `2` native cancellation or interrupted setup/wait, `64` invalid arguments. `--help` works without connecting. Compilation and command-line checks do not establish native interactive acceptance.
