#!/usr/bin/env python3
"""Require ordinary PD production, tests, and samples to remain Engine-first."""
from __future__ import annotations

from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]

FORBIDDEN_PACKAGES = {
    "CircuitHub.AllegroBridge.Contracts",
    "CircuitHub.AllegroBridge.Host",
    "CircuitHub.AllegroBridge.Licensing",
    "CircuitHub.AllegroBridge.Protocol",
    "CircuitHub.AllegroBridge.Sdk",
    "CircuitHub.AllegroBridge.Transport",
    "CircuitHub.AllegroBridge.Windows",
}
FORBIDDEN_SOURCE = re.compile(
    r"(?:using\s+(?:global::)?CircuitHub\.AllegroBridge\s*;)"
    r"|(?:using\s+(?:global::)?CircuitHub\.AllegroBridge\."
    r"(?:Contracts|Host|Licensing|Protocol|Sdk|Transport|Windows)\b)"
    r"|(?<![\"'])(?:(?:global::)?CircuitHub\.AllegroBridge\."
    r"(?:Contracts|Host|Licensing|Protocol|Sdk|Transport|Windows)\.[A-Z][A-Za-z0-9_]*)"
    r"|(?<![\"'])(?:(?:global::)?CircuitHub\.AllegroBridge\.Allegro[A-Za-z0-9_]*)"
)

# Bridge platform diagnostics: raw handshake/SDK probes (PD.BridgeProbe) that
# diagnose the Bridge connection below the Engine facade. They require the
# lower SDK by design, are owned by the Bridge platform (preferred home: the
# Bridge repository), and are excluded from the ordinary-consumer rule. This
# prefix list is exhaustive: nothing may be added here without Bridge-owner
# review, and production projects plus ordinary tests/samples must stay
# Engine-first with no other exception.
PLATFORM_DIAGNOSTIC_PREFIXES = (
    "tests/PD.NativeCampaign/BridgePlatformProbe/",
)


def is_platform_diagnostic(rel: str) -> bool:
    return rel.startswith(PLATFORM_DIAGNOSTIC_PREFIXES)


errors: list[str] = []
source_files = 0
platform_files = 0
for base in (ROOT / "src", ROOT / "tests", ROOT / "samples"):
    for path in sorted(base.rglob("*.cs")):
        if any(part in {"bin", "obj"} for part in path.parts):
            continue
        rel = path.relative_to(ROOT).as_posix()
        if is_platform_diagnostic(rel):
            platform_files += 1
            continue
        source_files += 1
        text = path.read_text(encoding="utf-8-sig")
        if FORBIDDEN_SOURCE.search(text):
            errors.append(
                f"{rel}: ordinary maintained C# source binds a lower Bridge layer; "
                "use CircuitHub.AllegroBridge.Engine or optional WPF."
            )

projects = sorted((ROOT / "src").rglob("*.csproj"))
projects.extend(sorted((ROOT / "tests").rglob("*.csproj")))
projects.extend(sorted((ROOT / "samples").rglob("*.csproj")))
platform_projects = 0
for project in projects:
    rel = project.relative_to(ROOT).as_posix()
    if is_platform_diagnostic(rel):
        platform_projects += 1
        continue
    tree = ET.parse(project)
    package_names = {
        element.attrib.get("Include")
        for element in tree.getroot().iter()
        if element.tag.endswith("PackageReference")
    }
    direct_forbidden = sorted(FORBIDDEN_PACKAGES.intersection(package_names))
    if direct_forbidden:
        errors.append(
            f"{rel}: direct lower-layer package reference(s): " +
            ", ".join(direct_forbidden)
        )

    reference_names = {
        element.attrib.get("Include")
        for element in tree.getroot().iter()
        if element.tag.endswith("Reference") and
        not element.tag.endswith("PackageReference") and
        not element.tag.endswith("ProjectReference")
    }
    direct_assembly = sorted(FORBIDDEN_PACKAGES.intersection(reference_names))
    if direct_assembly:
        errors.append(
            f"{rel}: direct lower-layer assembly reference(s): " +
            ", ".join(direct_assembly)
        )

    if rel.startswith("samples/"):
        project_references = [
            element.attrib.get("Include", "")
            for element in tree.getroot().iter()
            if element.tag.endswith("ProjectReference")
        ]
        if project_references:
            errors.append(
                f"{rel}: ordinary sample must be package-only, not ProjectReference-based."
            )
        if "AllegroBridgeSourceRoot" in project.read_text(encoding="utf-8-sig"):
            errors.append(f"{rel}: ordinary sample declares a platform source-root override.")

    is_product = rel in {
        "src/PD.PcbTools/PD.PcbTools.csproj",
        "src/PD.Simple/PD.Simple.csproj",
    }
    if (is_product or rel.startswith("samples/")) and \
            "CircuitHub.AllegroBridge.Engine" not in package_names:
        errors.append(f"{rel}: Engine must be an explicit dependency.")

    uses_wpf = any(
        element.tag.endswith("UseWPF") and
        (element.text or "").strip().lower() == "true"
        for element in tree.getroot().iter()
    )
    if "CircuitHub.AllegroBridge.Wpf" in package_names and not uses_wpf:
        errors.append(f"{rel}: WPF is referenced by a project that does not opt into UseWPF.")

if errors:
    print("Engine-primary boundary FAILED:", file=sys.stderr)
    for error in errors:
        print(" - " + error, file=sys.stderr)
    raise SystemExit(1)

print(
    "PASS: zero ordinary lower-SDK source/package/assembly seams across "
    f"{source_files} maintained C# files and {len(projects) - platform_projects} ordinary "
    f"production/test/sample projects; {platform_files} files in {platform_projects} "
    "Bridge platform-diagnostic project(s) excluded by documented scope: "
    + ", ".join(PLATFORM_DIAGNOSTIC_PREFIXES)
)
