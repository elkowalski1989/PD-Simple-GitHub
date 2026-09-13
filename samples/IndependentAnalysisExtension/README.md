# Independent Analysis Extension

`ProximityReviewExtension` demonstrates an application-defined interpretation
layer over public Engine query output. The application supplies an interpretation
ID, an outside-review margin, inclusion policy, and result budget. The extension
derives immutable review results and a new `AnnotationScene` from
`ProximityAnalysis`; it receives no live session or mutable scene owner.

The extension is intentionally a separate source unit and compiles using only
the public `CircuitHub.AllegroBridge.Engine` assembly. It does not add or alter
canonical scene families and does not rewrite an acquired scene. Its review
states are application terminology, not native rule or signoff results.

Session 01 owns persistent project and solution wiring. Lane 07 verifies this
extension as an independent assembly against the exact `LIVE_API_READY` Engine
checkpoint before handing the source off for integration.
