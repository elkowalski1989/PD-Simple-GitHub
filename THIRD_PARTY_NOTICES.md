# Dependency and source notices

## CircuitHub Allegro Bridge Engine

PD-Simple directly uses `CircuitHub.AllegroBridge.Engine` and optional
`CircuitHub.AllegroBridge.Wpf` version **1.14.4**, copyright 2026
CircuitHub. Engine.Core and the lower SDK/runtime are supplied transitively by the
matching local generation recorded in
`packages/release-bundle.1.14.4.json`. Extracted dependency caches
are not tracked.

## Cadence Allegro

Cadence Allegro is a separately installed, licensed runtime. It is not included
in this repository.

## Application provenance

The retained corridor and routing implementation was adapted from
PD Workflow Engine, as described in the root README.

The current build consumes the matching private Bridge 1.14.4 release.
Building PD does not change any package license or grant redistribution rights.
