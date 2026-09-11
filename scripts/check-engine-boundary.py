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

# Existing PD.Simple migration seams only. PD.PcbTools is now Engine-only.
SDK_ALLOWED = {
    "src/PD.Simple/BoardOverlayController.cs",
    "src/PD.Simple/BoardOverlayDrawingPolicy.cs",
    "src/PD.Simple/BridgeSession.cs",
    "src/PD.Simple/ConnectionSwitchPolicy.cs",
    "src/PD.Simple/InteractiveRouteRecovery.cs",
    "src/PD.Simple/SimpleToolExtension.cs",
    "src/PD.Simple/Corridor/DpViaCorridorBoardOverlay.cs",
    "src/PD.Simple/Corridor/DpViaCorridorNativeCapture.cs",
}

errors: list[str] = []
actual_sdk_seams: list[str] = []
for base in (ROOT / "src" / "PD.Simple", ROOT / "src" / "PD.PcbTools"):
    for path in sorted(base.rglob("*.cs")):
        rel = path.relative_to(ROOT).as_posix()
        text = path.read_text(encoding="utf-8-sig")
        direct_sdk = (
            "using CircuitHub.AllegroBridge;" in text
            or "CircuitHub.AllegroBridge.Allegro" in text
        )
        if direct_sdk:
            actual_sdk_seams.append(rel)
            if rel not in SDK_ALLOWED:
                errors.append(
                    f"{rel}: new direct SDK usage is outside the migration boundary; "
                    "use CircuitHub.AllegroBridge.Engine or document a deliberate adapter seam."
                )

stale = sorted(SDK_ALLOWED.difference(actual_sdk_seams))
if stale:
    errors.append("SDK allowlist contains migrated/stale seams: " + ", ".join(stale))

pcb_tools = ROOT / "src" / "PD.PcbTools" / "PD.PcbTools.csproj"
pd_simple = ROOT / "src" / "PD.Simple" / "PD.Simple.csproj"
for project in (pd_simple, pcb_tools):
    tree = ET.parse(project)
    names = {
        element.attrib.get("Include")
        for element in tree.getroot().iter()
        if element.tag.endswith("PackageReference")
    }
    if "CircuitHub.AllegroBridge.Engine" not in names:
        errors.append(f"{project.relative_to(ROOT)}: Engine must be an explicit dependency.")
    if project == pcb_tools and "CircuitHub.AllegroBridge.Sdk" in names:
        errors.append("src/PD.PcbTools/PD.PcbTools.csproj: reusable engineering policy must not reference the SDK directly.")

if errors:
    print("Engine-primary boundary FAILED:", file=sys.stderr)
    for error in errors:
        print(" - " + error, file=sys.stderr)
    raise SystemExit(1)

print(f"PASS: PD.PcbTools is Engine-only and PD.Simple direct SDK usage is confined to {len(actual_sdk_seams)} documented migration seams.")
