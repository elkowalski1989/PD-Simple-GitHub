#!/usr/bin/env python3
"""Keep new PD application/tool code on the Engine-primary architecture.

The allowlist is deliberate migration debt. Existing low-level/native adapters may
use CircuitHub.AllegroBridge SDK types until an equivalent Engine live authority
is source-available and qualified. New files do not get added to the allowlist
without explaining why Engine cannot own the capability yet.
"""
from __future__ import annotations

from pathlib import Path
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]

# Existing migration seams only. Keep this shrinking.
SDK_ALLOWED = {
    "src/PD.PcbTools/CorridorAnalyzer.cs",
    "src/PD.PcbTools/CorridorNavigation.cs",
    "src/PD.PcbTools/HorizontalFirstPlanner.cs",
    "src/PD.Simple/BoardOverlayController.cs",
    "src/PD.Simple/BoardOverlayDrawingPolicy.cs",
    "src/PD.Simple/BoardOverlayHud.cs",
    "src/PD.Simple/BridgeSession.cs",
    "src/PD.Simple/BridgeSession.PcbTools.cs",
    "src/PD.Simple/ConnectionSwitchPolicy.cs",
    "src/PD.Simple/InteractiveRouteCompletion.cs",
    "src/PD.Simple/InteractiveRouteOverlayFeedbackState.cs",
    "src/PD.Simple/Corridor/DpViaCorridorNativeCapture.cs",
    "src/PD.Simple/Corridor/DpViaCorridorResult.cs",
    "src/PD.Simple/Corridor/DpViaCorridorZoomResult.cs",
    "src/PD.Simple/Corridor/IDpViaCorridorService.cs",
}

errors: list[str] = []
for base in (ROOT / "src" / "PD.Simple", ROOT / "src" / "PD.PcbTools"):
    for path in sorted(base.rglob("*.cs")):
        rel = path.relative_to(ROOT).as_posix()
        text = path.read_text(encoding="utf-8-sig")
        direct_sdk = (
            "using CircuitHub.AllegroBridge;" in text
            or "CircuitHub.AllegroBridge.Allegro" in text
        )
        if direct_sdk and rel not in SDK_ALLOWED:
            errors.append(
                f"{rel}: new direct SDK usage is outside the migration boundary; "
                "use CircuitHub.AllegroBridge.Engine or document a deliberate adapter seam."
            )

for project in (ROOT / "src" / "PD.Simple" / "PD.Simple.csproj",
                ROOT / "src" / "PD.PcbTools" / "PD.PcbTools.csproj"):
    tree = ET.parse(project)
    names = {
        element.attrib.get("Include")
        for element in tree.getroot().iter()
        if element.tag.endswith("PackageReference")
    }
    if "CircuitHub.AllegroBridge.Engine" not in names:
        errors.append(f"{project.relative_to(ROOT)}: Engine must be an explicit dependency.")

if errors:
    print("Engine-primary boundary FAILED:", file=sys.stderr)
    for error in errors:
        print(" - " + error, file=sys.stderr)
    raise SystemExit(1)

print(f"PASS: Engine is explicit and direct SDK usage is confined to {len(SDK_ALLOWED)} documented migration seams.")
