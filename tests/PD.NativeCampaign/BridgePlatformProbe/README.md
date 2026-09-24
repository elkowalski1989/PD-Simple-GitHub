# Bridge platform probes (advanced diagnostics)

`PD.BridgeProbe` holds raw handshake/SDK probes that diagnose the Bridge
connection **below** the Engine facade: the real handshake failure behind a
connection timeout, raw host behavior, region/target/Drc reads. They bind
`CircuitHub.AllegroBridge.Sdk` and lower Bridge layers **by design** — that
is what they probe.

## Boundary

- These probes are **Bridge platform diagnostics**, not ordinary PD
  consumer tests. Do not import their types from product code, samples, or
  ordinary tests, and do not add product dependencies on this project.
- The preferred home for genuine platform probes is the Bridge repository.
  Repository movement is outside this workspace's authorization, so they
  stay here isolated under this explicitly named directory.
- `scripts/check-engine-boundary.py` excludes exactly this directory prefix
  from the ordinary-consumer Engine-first rule and reports the exclusion in
  its PASS line. The prefix list is exhaustive: nothing may be added to it
  without Bridge-owner review.
- `src/PD.Simple` and `src/PD.PcbTools`, and every other test/sample
  project, must remain Engine-first with no other exception.
