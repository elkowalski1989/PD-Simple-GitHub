#!/usr/bin/env python3
"""Check the source ZIP, then exercise only its extracted package-only content."""

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path, PurePosixPath
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
import zipfile


REQUIRED = {
    "Build.ps1",
    "NuGet.Config",
    "Directory.Build.props",
    "Directory.Build.targets",
    "scripts/check-engine-boundary.py",
    "scripts/check-source-archive.py",
    "scripts/package-source.py",
    "docs/Development-Acceptance.md",
    "docs/BUILDING-A-PD-TOOL.md",
    "src/PD.Simple/PD.Simple.csproj",
    "src/PD.PcbTools/PD.PcbTools.csproj",
    "installer/Install.ps1",
}
policy_spec = importlib.util.spec_from_file_location("pd_source_policy", Path(__file__).with_name("package-source.py"))
source_policy = importlib.util.module_from_spec(policy_spec)
policy_spec.loader.exec_module(source_policy)


def archive_members(archive):
    members = {}
    for entry in archive.infolist():
        path = PurePosixPath(entry.filename.replace("\\", "/"))
        if (path.is_absolute() or ".." in path.parts or not path.parts or path.parts[0] != "PD-Simple"
                or ":" in entry.filename or any(ord(character) < 32 for character in entry.filename)):
            raise ValueError(f"Unsafe or unexpected source entry: {entry.filename}")
        if entry.is_dir():
            continue
        relative = PurePosixPath(*path.parts[1:]).as_posix()
        if relative.casefold() in members:
            raise ValueError(f"Duplicate source entry: {entry.filename}")
        if source_policy.is_excluded_path(path):
            raise ValueError(f"Excluded source entry: {entry.filename}")
        members[relative.casefold()] = (relative, entry)
    missing = REQUIRED - {relative for relative, _ in members.values()}
    if missing:
        raise ValueError("Source archive is missing required files: " + ", ".join(sorted(missing)))
    return members


def verify_packages(root):
    version, packages, expected_files = source_policy.selected_package_files(root)
    actual_files = {path for path in (root / "packages").rglob("*") if path.is_file()}
    if actual_files != set(expected_files):
        raise ValueError("The archive package payload must contain only the six active packages, exact manifest, README, and declared active starter archive.")
    for item in packages:
        package = root / "packages" / item["file"]
        with zipfile.ZipFile(package) as archive:
            specs = [name for name in archive.namelist() if name.endswith(".nuspec")]
            if len(specs) != 1:
                raise ValueError(f"Package has no unambiguous nuspec: {package.name}")
            spec = ET.fromstring(archive.read(specs[0]))
            identities = [node.text for node in spec.iter() if node.tag.split("}")[-1] == "id"]
            versions = [node.text for node in spec.iter() if node.tag.split("}")[-1] == "version"]
            if identities != [item["id"]] or versions != [version]:
                raise ValueError(f"Package nuspec identity differs from the active manifest: {package.name}")
            for dependency in spec.iter():
                if dependency.tag.split("}")[-1] == "dependency" and dependency.attrib.get("id") in source_policy.PACKAGE_IDS:
                    if dependency.attrib.get("version") != f"[{version}]":
                        raise ValueError(f"Package dependency is not exact: {package.name}")


def run(command, root, environment):
    print("RUN", " ".join(map(str, command)), flush=True)
    subprocess.run(command, cwd=root, env=environment, check=True)


def check_archive(path, run_managed):
    with tempfile.TemporaryDirectory(prefix="pd-source-acceptance-") as temporary:
        extraction = Path(temporary)
        with zipfile.ZipFile(path) as archive:
            members = archive_members(archive)
            for relative, entry in members.values():
                destination = extraction / "PD-Simple" / relative
                destination.parent.mkdir(parents=True, exist_ok=True)
                with archive.open(entry) as source, destination.open("xb") as target:
                    while chunk := source.read(1024 * 1024):
                        target.write(chunk)
        root = extraction / "PD-Simple"
        verify_packages(root)
        environment = os.environ.copy()
        environment.pop("AllegroBridgeSourceRoot", None)
        environment["NUGET_PACKAGES"] = str(extraction / "nuget-cache")
        run([sys.executable, "scripts/check-engine-boundary.py"], root, environment)
        if run_managed:
            checks = [
                "PD.Simple.Checks", "PD.PcbTools.Checks", "PD.ToolsB.Checks",
                "PD.ToolsConformance", "PD.Simple.DrawingChecks", "PD.EngineBoundaryChecks",
            ]
            samples = [
                "BoardExplorer/BoardExplorer", "ReadPcb/ReadPcb", "PickAndMeasure/PickAndMeasure",
                "IndependentAnalysisExtension/IndependentAnalysisExtension",
                "ViaProximityExplorer/ViaProximityExplorer",
                "ViaProximityExplorer/ViaProximityExplorer.Presentation",
            ]
            projects = ["src/PD.Simple/PD.Simple.csproj"]
            projects += [f"tests/{name}/{name}.csproj" for name in checks]
            projects += [f"samples/{name}.csproj" for name in samples]
            for project in projects:
                run(["dotnet", "restore", project, "--configfile", "NuGet.Config"], root, environment)
                run(["dotnet", "build", project, "-c", "Release", "--no-restore", "-m:1"], root, environment)
            for name in checks:
                run(["dotnet", "run", "--project", f"tests/{name}/{name}.csproj",
                     "-c", "Release", "--no-build", "--no-restore"], root, environment)
        print(f"PASS: {len(members)} source entries; extracted boundary and exact package provenance verified.")


def self_test():
    with tempfile.TemporaryDirectory(prefix="pd-source-negative-") as temporary:
        for missing in [None, "scripts/check-engine-boundary.py"]:
            path = Path(temporary) / ("complete.zip" if missing is None else "missing-script.zip")
            with zipfile.ZipFile(path, "w") as archive:
                for name in REQUIRED:
                    if name != missing:
                        archive.writestr("PD-Simple\\" + name.replace("/", "\\"), b"fixture")
            with zipfile.ZipFile(path) as archive:
                try:
                    archive_members(archive)
                except ValueError:
                    if missing is None:
                        raise
                else:
                    if missing is not None:
                        raise AssertionError("The negative archive missing the boundary script was accepted.")
        package_payload_controls(Path(temporary))
    print("PASS: source inventory, active package/starter provenance, and missing/stale/extra/tampered payload controls.")


def package_payload_controls(temporary):
    root = temporary / "fixture"
    authored = set(source_policy.SOURCE_ROOT_FILES) | REQUIRED | {"packages/README.md"}
    authored |= {"scripts/" + name for name in source_policy.SOURCE_SCRIPTS}
    for name in authored:
        path = root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("Source fixture\n", encoding="utf-8")
    version = "9.8.7-sourcecheck.5"
    (root / "Directory.Build.props").write_text(
        f"<Project><PropertyGroup><AllegroBridgePackageVersion>{version}</AllegroBridgePackageVersion>"
        "</PropertyGroup></Project>", encoding="utf-8")
    packages = []
    for package_id in sorted(source_policy.PACKAGE_IDS):
        path = root / "packages" / f"{package_id}.{version}.nupkg"
        with zipfile.ZipFile(path, "w") as archive:
            archive.writestr(package_id + ".nuspec",
                             f"<package><metadata><id>{package_id}</id><version>{version}</version></metadata></package>")
        packages.append({"id": package_id, "version": version, "file": path.name,
                         "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
    starter = root / "packages" / f"allegro-engine-starters.{version}.zip"
    starter_entries = ["console/Program.cs", "portable-review-reader/Program.cs"]
    with zipfile.ZipFile(starter, "w") as archive:
        for name in starter_entries:
            archive.writestr(name, "starter fixture")
    manifest = root / "packages" / f"development-bundle.{version}.json"
    manifest.write_text(json.dumps({"schema": 2, "kind": "unsigned-development-candidate", "packages": packages, "starters": {
        "archive": starter.name, "version": version,
        "archive_sha256": hashlib.sha256(starter.read_bytes()).hexdigest(), "entries": starter_entries,
    }}), encoding="utf-8")
    historical = root / "packages" / "CircuitHub.AllegroBridge.Sdk.0.0.1.nupkg"
    historical.write_bytes(b"historical private fixture")
    historical_starter = root / "packages" / "allegro-engine-starters.0.0.1.zip"
    historical_starter.write_bytes(b"historical starter fixture")
    complete = source_policy.package_source(root, temporary / "selected-source.zip")
    with zipfile.ZipFile(complete) as archive:
        contents = {entry.filename: archive.read(entry) for entry in archive.infolist()}
    if ("PD-Simple/packages/" + historical.name in contents or not historical.exists()
            or "PD-Simple/packages/" + historical_starter.name in contents or not historical_starter.exists()):
        raise AssertionError("Source packaging included or removed historical package bytes.")
    selected_name = "PD-Simple/packages/" + packages[0]["file"]
    for fault in [None, "schema-one", "release", "ambiguous", "wrong-kind", "missing-kind", "missing", "stale", "extra", "tampered", "missing-starter",
                  "tampered-starter", "starter-version", "starter-inventory", "drive-path"]:
        changed = dict(contents)
        if fault in {"schema-one", "release", "ambiguous", "wrong-kind", "missing-kind", "starter-version", "starter-inventory"}:
            manifest_name = "PD-Simple/packages/" + manifest.name
            metadata = json.loads(changed[manifest_name])
            if fault == "schema-one":
                metadata["schema"] = 1
                del metadata["starters"]
                del changed["PD-Simple/packages/" + starter.name]
            elif fault in {"release", "ambiguous"}:
                metadata.update({"schema": 3, "kind": "signed-release-candidate", "source_commit": "a" * 40,
                                 "source_modified": False, "source_diff_sha256": None, "host_sha256": "b" * 64,
                                 "native_gui_acceptance": "not_run",
                                 "signing": {"status": "verified", "certificate_thumbprint": "c" * 40}})
                if fault == "release":
                    del changed[manifest_name]
                manifest_name = f"PD-Simple/packages/release-bundle.{version}.json"
            elif fault == "wrong-kind":
                metadata["kind"] = "signed-release-candidate"
            elif fault == "missing-kind":
                metadata["schema"] = 1
                del metadata["kind"]
            elif fault == "starter-version":
                metadata["starters"]["version"] = "0.0.1"
            else:
                metadata["starters"]["entries"].append("missing/Program.cs")
            changed[manifest_name] = json.dumps(metadata).encode("utf-8")
        elif fault == "missing":
            del changed[selected_name]
        elif fault == "stale":
            changed["PD-Simple/packages/" + historical.name] = b"stale"
        elif fault == "extra":
            changed[f"PD-Simple/packages/Unexpected.{version}.nupkg"] = b"extra"
        elif fault == "tampered":
            changed[selected_name] += b"changed"
        elif fault == "missing-starter":
            del changed["PD-Simple/packages/" + starter.name]
        elif fault == "tampered-starter":
            changed["PD-Simple/packages/" + starter.name] += b"changed"
        elif fault == "drive-path":
            changed["PD-Simple/C:/escaped.txt"] = b"unsafe"
        path = temporary / f"package-{fault or 'complete'}.zip"
        with zipfile.ZipFile(path, "w") as archive:
            for name, data in changed.items():
                archive.writestr(name, data)
        try:
            extraction = temporary / f"extracted-{fault or 'complete'}"
            with zipfile.ZipFile(path) as archive:
                members = archive_members(archive)
                for relative, entry in members.values():
                    destination = extraction / relative
                    destination.parent.mkdir(parents=True, exist_ok=True)
                    destination.write_bytes(archive.read(entry))
            verify_packages(extraction)
        except ValueError:
            if fault in {None, "schema-one", "release"}:
                raise
        else:
            if fault not in {None, "schema-one", "release"}:
                raise AssertionError(f"The source archive accepted the {fault} package-payload control.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive", nargs="?", type=Path)
    parser.add_argument("--run-managed", action="store_true", help="Run the documented Windows managed journey.")
    parser.add_argument("--self-test", action="store_true")
    arguments = parser.parse_args()
    if arguments.self_test:
        self_test()
    elif arguments.archive is not None:
        check_archive(arguments.archive, arguments.run_managed)
    else:
        parser.error("Provide a source ZIP or --self-test.")
