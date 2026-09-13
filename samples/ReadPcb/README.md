# Read PCB geometry through Engine

A package-only .NET 10 console example that directly references only
`CircuitHub.AllegroBridge.Engine`. It has no PD Simple dependency, project
reference, linked source, source-root override, or consumer SKILL.

Build against the coordinator's exact package candidate:

```powershell
dotnet build samples/ReadPcb/ReadPcb.csproj `
  -p:AllegroBridgePackageVersion='<exact-version>'
```

Run on Windows with Allegro open and an existing matching Bridge session:

```powershell
dotnet run --project samples/ReadPcb `
  -p:AllegroBridgePackageVersion='<exact-version>' -- `
  --bridge-dir "C:\path\to\active-bridge" `
  --net "MY_SIGNAL_P" --net "MY_SIGNAL_N" --maximum-results 512
```

`AllegroEngineDiscovery.ResolveLaunchTarget` validates the explicit directory,
`AllegroEngineSession` owns the connection, and the stable workspace requests
`SceneQuery.BoardGeometry`. The sample then filters 1–32 unique, case-sensitive
net names in the immutable canonical scene and prints up to the requested number
of matching copper records.

The current Engine API has no net-scoped native acquisition equivalent. Unlike
the former SDK example, this performs a coherent board-geometry acquisition
before filtering; `--maximum-results` limits output, not native work. The result
prints document/capture identity and copper availability, completeness, fidelity,
truncation, and reasons. Missing or incomplete data is not empty space or a
clearance result.

Ctrl+C stops the managed connection/read wait. No mutation, native replay, Host
activation outside the normal Engine path, or direct lower-layer API is requested.
Exit codes: `0` usable scene (possibly incomplete), `1` failure, `2` canceled
wait, `64` invalid arguments. `--help` works without connecting.
