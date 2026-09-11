# Board Explorer

A compact read-only WPF example of the public Allegro Bridge SDK. It has no
PD-Simple project reference, consumer SKILL, private API or raw database-ID access.
It changes only the supported bridge-owned highlight/view when requested.

First put the matching **1.12.0-preview.2 SDK and WPF** packages into the controlled
feed described in the root README. From the checkout:

```powershell
dotnet run --project samples/BoardExplorer/BoardExplorer.csproj -c Release
```

Choose **Find boards**, select the intended board, then **Connect**. Native
attachment verifies the resident/window identity instead of trusting a title or
PID. Start the matching Bridge in Allegro first if no usable session exists.

Search components, nets, declared pairs or layers. **Read pins** explicitly
requests a component's string pin IDs and connectivity; it does not load copper.
**Highlight** and **Zoom** resolve a supported named object in the current board.
**Copy C#** produces the corresponding public query, with escaped identifiers.
Refresh is explicit because captured metadata is not a live mutable database.

Collection availability remains visible. Empty differs from not requested or
unavailable. Part numbers can repeat, unplaced components are retained, and a
null net is a disconnected pin. Pair Side A/B is not positive/negative polarity.
Closing disposes this client; it does not close the user's Allegro application.

The build can use the root compiler-only source-reference mode for maintained
syntax checks. It is not a runnable runtime handoff or native GUI verification.
