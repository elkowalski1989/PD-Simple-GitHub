# Read PCB geometry

A package-only .NET 10 console example using `CircuitHub.AllegroBridge.Sdk` **1.12.0-preview.2**. It has no PD Simple dependency, project references or consumer SKILL.

Build from this repository root:

```powershell
dotnet build samples/ReadPcb/ReadPcb.csproj
```

Run on Windows with Allegro open and an existing bridge session. Replace the directory with that session's actual bridge directory and replace the names with exact nets from your board:

```powershell
dotnet run --project samples/ReadPcb -- --bridge-dir "C:\path\to\active-bridge" --net "MY_SIGNAL_P" --net "MY_SIGNAL_N" --maximum-objects 512
```

This does not launch Allegro, create a bridge session directory, install certificates or edit board geometry. The repository's `NuGet.Config` resolves the exact SDK from `packages/`; copying this example elsewhere also requires a feed containing that release package.

The example opens the typed PCB facade, checks its read capability and queries 1–32 exact, case-sensitive net names. The object limit is shared across returned segments, vias and pins. It prints the native receipt and geometry, including session/board identity, mil coordinates, native units/precision, truncation and unavailable categories.

This is a bounded net read, not full-board copper or a clearance check. Missing categories and truncated results must not be interpreted as empty space. Ctrl+C stops the managed read wait; it does not claim native cancellation. No mutation is requested.

Exit codes: `0` verified read (possibly bounded/incomplete coverage), `1` failure, `2` canceled wait, `64` invalid arguments. `--help` works without connecting.

Build the matching development packages first; the retained preview.1 files do not satisfy this source candidate. See the root README.
