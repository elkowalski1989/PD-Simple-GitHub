# Dependency and source notices

## CircuitHub Allegro Bridge Engine

PD-Simple directly uses `CircuitHub.AllegroBridge.Engine` and optional
`CircuitHub.AllegroBridge.Wpf` version **1.13.0-preview.16**, copyright 2026
CircuitHub. Engine.Core and the lower SDK/runtime are supplied transitively by the
matching local generation recorded in
`packages/development-bundle.1.13.0-preview.16.json`. Extracted dependency caches
are not tracked.

## Cadence Allegro

Cadence Allegro is a separately installed, licensed runtime. It is not included
in this repository.

## Application provenance

The retained corridor and routing implementation was adapted from
PD Workflow Engine, as described in the root README.

Current source changes target the matching 1.12.0-preview.2 development bundle.
The older supplied files described above remain unchanged. Building a development
candidate does not change any package license or grant redistribution rights.
