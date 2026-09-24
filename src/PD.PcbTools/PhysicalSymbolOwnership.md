# Physical symbols ownership boundary

PD page composition stays in `PD.PcbTools`; Engine owns the symbol
operation contracts. This file records the split so the next move is
mechanical. Physical moves of generic pieces into the Engine package
await Bridge workspace authorization; until then the facade below is
the only PD contact point with Engine-owned identities.

## PD-owned (stays in PD.PcbTools)

- Page composition: `PhysicalSymbolTool.Registration`,
  `DescribeActions` page list, `SummarizeDefinitions` catalog facts.
- PD registry shapes: `PhysicalSymbolToolActions` (`symbol.*` ids),
  `PhysicalSymbolToolAvailability`, `PhysicalSymbolDefinitionSummary`,
  `PhysicalSymbolToolRegistration`.
- Presentation labels through `PhysicalSymbolOperationLabels` (facade
  version 1): curated titles resolved by Engine numeric identity.
- Honesty disclosures authored by PD: `VendorLimits` entries.

## Engine-owned (referenced, never duplicated)

- `EnginePhysicalSymbolOperation` and
  `EnginePhysicalSymbolCapabilityGroup` contracts, values, and any
  future members.
- `EnginePhysicalSymbolCapabilities` diagnostics, qualification,
  supported/acceptance operation lists.
- Staged PACKAGE workflows: `EngineSymbolWorkArea`,
  `EngineSymbolBindingRunner`, `EnginePhysicalSymbolPublisher`.

## Facade rule

PD resolves Engine operation/group identities by numeric value through
`PhysicalSymbolOperationLabels`. Member names appear only in the
value-pin tests. Consequences:

- An Engine rename keeps its value and flows without PD edits
  (proven by `PhysicalSymbolFacadeChecks`: id-cast lookups equal
  member lookups).
- An Engine revalue fails the pinned identities loudly instead of
  mislabeling the page.
- A future unknown identity renders an explicit fallback
  (`Symbol operation {id}` / `Group {id}`), never a curated title
  and never an exception.

## Move protocol for the next piece

1. Establish behavior checks for the piece's current PD entry points.
2. Move one piece at a time behind the facade (or into Engine once
   authorized) with zero behavior change.
3. Extend the facade equality tests so any PD-side duplicate that
   diverges from Engine metadata fails.
