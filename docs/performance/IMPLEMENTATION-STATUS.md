# Acquisition experiment status

The new console adapter calls the actual public Engine and PD.PcbTools analyzer.
It is not wired into production navigation or Run. Existing package pins and
product behavior are unchanged.

Compilation is pending in the supported .NET 10 environment. No actual Allegro
run or new speedup is claimed. The matching Bridge branch contains the runner,
25 passing Python tests, guarded cancellation source experiments, native walk
probe, batched-demand/memo prototypes and detailed qualification protocol.

Read Bridge `docs/performance/MEASUREMENT-REPORT.md` and
`docs/performance/ACQUISITION-PERFORMANCE.md`, then this repository's
`docs/performance/ACQUISITION-BENCHMARK.md`.

Use explicit fresh acquisitions for same-file reopen testing. Keep Browse
separate from strict selected-field verification. Record visible UI latency with
the actual PD UI campaign; the console adapter only measures Engine completion.
