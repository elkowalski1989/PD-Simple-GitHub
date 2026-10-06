# Dependency and source notices

## CircuitHub PCB SDK

PD-Simple directly uses `CircuitHub.PcbSdk` and optional
`CircuitHub.PcbSdk.Wpf` version **2.0.0**, copyright 2026
CircuitHub. Engine.Core and the lower SDK/runtime are supplied transitively by the
matching local generation recorded in
`packages/release-bundle.2.0.0.json`. Extracted dependency caches
are not tracked.

## Cadence Allegro

Cadence Allegro is a separately installed, licensed runtime. It is not included
in this repository.

## Application provenance

The retained corridor and routing implementation was adapted from
PD Workflow Engine, as described in the root README.

The current build consumes the matching private PCB SDK 2.0.0 generation.
Building PD does not change any package license or grant redistribution rights.
