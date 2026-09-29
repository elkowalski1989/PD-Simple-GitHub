#!/usr/bin/env python3
"""Create the independently usable PD source ZIP without publishing a runtime."""

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import tempfile
import uuid
import xml.etree.ElementTree as ET
import zipfile


SOURCE_ROOT_FILES = (
    "README.md", "CONTRIBUTING.md", "THIRD_PARTY_NOTICES.md",
    "Build.cmd", "Build.ps1", "NuGet.Config", "Directory.Build.props",
    "Directory.Build.targets", ".gitignore", ".gitattributes", ".editorconfig",
)
SOURCE_TREES = ("src", "installer", "tests", "samples", "docs")
PACKAGE_IDS = {
    "CircuitHub.AllegroBridge.Sdk",
    "CircuitHub.AllegroBridge.Engine.Core",
    "CircuitHub.AllegroBridge.Engine",
    "CircuitHub.AllegroBridge.Wpf",
    "CircuitHub.AllegroBridge.Wpf.NativeHost",
    "CircuitHub.AllegroBridge.Wpf.NativeHost.Engine",
}
SOURCE_SCRIPTS = (
    "check-engine-boundary.py", "check-source-archive.py", "package-source.py",
    "rebuild-with-local-bridge.ps1", "rebuild-with-local-bridge.bat",
)
EXCLUDED_PARTS = {"bin", "obj", "artifacts", "_local-runs", "__pycache__", ".git", ".vs", "node_modules"}
EXCLUDED_NAME = re.compile(
    r"(\.local\.|\.(log|brd|dra|pad|dbk|jrl|pyc|pfx|p12|pem|key)$|^\.env|credential|private[-_]?key|license[-_]?secret)",
    re.IGNORECASE,
)


def is_excluded_path(path):
    return bool(EXCLUDED_PARTS.intersection(part.lower() for part in path.parts) or EXCLUDED_NAME.search(path.name))


def require_file_hash(path, digest):
    if not isinstance(digest, str) or not re.fullmatch(r"[0-9A-Fa-f]{64}", digest):
        raise ValueError(f"The active manifest has an invalid file digest: {path.name}")
    if not path.is_file() or path.is_symlink():
        raise ValueError(f"The active generation file is missing or redirected: {path.name}")
    actual = hashlib.sha256()
    with path.open("rb") as source:
        while chunk := source.read(1024 * 1024):
            actual.update(chunk)
    if actual.hexdigest() != digest.lower():
        raise ValueError(f"Active generation file hash mismatch: {path.name}")


def selected_starter_file(package_root, version, starters):
    expected_name = f"allegro-engine-starters.{version}.zip"
    if starters.get("version") != version or starters.get("archive") != expected_name:
        raise ValueError("The starter archive must match the active generation exactly.")
    path = package_root / expected_name
    require_file_hash(path, starters.get("archive_sha256"))
    entries = starters.get("entries")
    if not isinstance(entries, list) or not entries or any(not isinstance(name, str) for name in entries):
        raise ValueError("The active starter manifest requires its exact entry inventory.")
    with zipfile.ZipFile(path) as archive:
        actual = [entry.filename for entry in archive.infolist() if not entry.is_dir()]
    for names in (entries, actual):
        if len({name.casefold() for name in names}) != len(names):
            raise ValueError("The active starter inventory contains duplicate entries.")
        for name in names:
            entry = PurePosixPath(name)
            if (entry.is_absolute() or ".." in entry.parts or not entry.parts or "\\" in name or ":" in name
                    or any(ord(character) < 32 for character in name)):
                raise ValueError("The active starter inventory contains an unsafe entry.")
    if sorted(actual) != sorted(entries):
        raise ValueError("The active starter ZIP inventory differs from its manifest.")
    return path


def selected_manifest(package_root, version):
    candidates = [package_root / f"{prefix}.{version}.json" for prefix in ("development-bundle", "release-bundle")]
    present = [path for path in candidates if path.exists()]
    if len(present) != 1 or not present[0].is_file() or present[0].is_symlink():
        raise ValueError("The active generation requires exactly one ordinary development or release bundle manifest.")
    path = present[0]
    manifest = json.loads(path.read_text(encoding="utf-8-sig"))
    schema = manifest.get("schema")
    if path.name.startswith("development-bundle."):
        if (type(schema) is not int or schema not in (1, 2) or manifest.get("kind") != "unsigned-development-candidate"
                or manifest.get("signing") is not None):
            raise ValueError("A development bundle must use its unsigned schema 1 or 2 provenance shape.")
    else:
        signing = manifest.get("signing")
        if (schema != 3 or manifest.get("kind") != "signed-release-candidate"
                or manifest.get("source_modified") is not False or manifest.get("source_diff_sha256") not in (None, "")
                or not is_lower_hex(manifest.get("source_commit"), 40)
                or not is_lower_hex(manifest.get("host_sha256"), 64)
                or manifest.get("native_gui_acceptance") != "not_run"
                or not isinstance(signing, dict) or signing.get("status") != "verified"
                or not is_lower_hex(signing.get("certificate_thumbprint"), 40)):
            raise ValueError("A release bundle must retain its schema 3 clean-source and signing provenance shape.")
    # These are metadata and inventory checks, not Authenticode or native qualification.
    return path, manifest


def is_lower_hex(value, length):
    return isinstance(value, str) and len(value) == length and all(character in "0123456789abcdef" for character in value)


def selected_package_files(root):
    version_nodes = ET.parse(root / "Directory.Build.props").findall(".//AllegroBridgePackageVersion")
    if len(version_nodes) != 1 or not version_nodes[0].text:
        raise ValueError("Source packaging requires one exact active Bridge generation.")
    version = version_nodes[0].text.strip()
    if not re.fullmatch(r"[0-9A-Za-z][0-9A-Za-z.+-]*", version):
        raise ValueError("The active Bridge package version is not an exact safe version.")
    package_root = root / "packages"
    manifest_path, manifest = selected_manifest(package_root, version)
    packages = manifest.get("packages", [])
    if len(packages) != 6 or {item.get("id") for item in packages} != PACKAGE_IDS:
        raise ValueError("The active manifest must identify exactly the six Bridge packages.")
    paths = [manifest_path, package_root / "README.md"]
    for item in packages:
        expected_name = f"{item['id']}.{version}.nupkg"
        if item.get("version") != version or item.get("file") != expected_name:
            raise ValueError("The active package manifest has a mixed identity or filename.")
        path = package_root / expected_name
        require_file_hash(path, item.get("sha256"))
        paths.append(path)
    if not paths[1].is_file() or paths[1].is_symlink():
        raise ValueError("The source archive requires the ordinary packages/README.md.")
    if manifest.get("starters") is not None:
        paths.append(selected_starter_file(package_root, version, manifest["starters"]))
    return version, packages, paths


def source_files(root):
    paths = [root / name for name in SOURCE_ROOT_FILES]
    paths += [root / "scripts" / name for name in SOURCE_SCRIPTS]
    _, _, selected_packages = selected_package_files(root)
    paths += selected_packages
    for tree in SOURCE_TREES:
        folder = root / tree
        if folder.is_dir():
            paths += [
                path for path in folder.rglob("*")
                if path.is_file() and not is_excluded_path(path.relative_to(root))
            ]
    for path in sorted(set(paths)):
        if not path.is_file() or path.is_symlink():
            raise ValueError(f"Source packaging requires an ordinary authored file: {path}")
        yield path


def package_source(root, output):
    root = root.resolve()
    output = output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    files = list(source_files(root))
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(mode="w+b", prefix=".pd-source-", suffix=".tmp",
                                         dir=output.parent, delete=False) as stream:
            temporary = Path(stream.name)
            with zipfile.ZipFile(stream, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
                for path in files:
                    relative = path.relative_to(root).as_posix()
                    archive.write(path, "PD-Simple/" + relative)
            stream.flush()
            os.fsync(stream.fileno())
        if output.exists():
            output.rename(output.with_name(output.name + ".previous-" + uuid.uuid4().hex))
        # The destination must still be absent; this never truncates another writer's file.
        os.link(temporary, output)
        temporary.unlink()
        temporary = None
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)
    print(f"Created {output} with {len(files)} source entries.")
    return output


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent)
    parser.add_argument("--output", type=Path)
    arguments = parser.parse_args()
    destination = arguments.output or arguments.root / "artifacts" / "PD-Simple-Source.zip"
    package_source(arguments.root, destination)
