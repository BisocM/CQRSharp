#!/usr/bin/env python3
"""Validates the packed CQRSharp packages. Shared by CI (structure only) and the release workflow (--check-nuget).

    python3 .github/scripts/validate_packages.py <package-dir> [--check-nuget]

Structure: the exact expected set of packages, one aligned version, every library built for every target framework with
its XML docs and a symbol package, the meta-package's embedded generator, the analyzers shipped exactly once (in
CQRSharp.Abstractions) and flowing through every dependency on another CQRSharp package, the template's pinned version,
and the shared README / license / icon in each package.

--check-nuget additionally asks nuget.org which packages already have this version and writes `should_publish` (whether
anything is left to push, so a push to Release that did not change the version publishes nothing) and `package_version`
(which the release job tags and takes the changelog section of) to $GITHUB_OUTPUT.
"""
from __future__ import annotations

import glob
import json
import os
import re
import sys
import urllib.error
import urllib.request
import xml.etree.ElementTree as ElementTree
import zipfile

META = "CQRSharp"
TEMPLATES = "CQRSharp.Templates"
LIBRARIES = {
    "CQRSharp.Abstractions",
    "CQRSharp.Core",
    "CQRSharp.Pipelines",
    "CQRSharp.Redis",
    "CQRSharp.EntityFrameworkCore",
    "CQRSharp.FluentValidation",
    "CQRSharp.AspNetCore",
    "CQRSharp.Testing",
    "CQRSharp.Testing.Xunit.V3",
}
EXPECTED = LIBRARIES | {META, TEMPLATES}
FRAMEWORKS = ("net8.0", "net9.0", "net10.0")
# The contracts also compile for netstandard2.0 consumers (a shared contracts library, for example).
EXTRA_FRAMEWORKS = {"CQRSharp.Abstractions": ("netstandard2.0",)}
SHARED_FILES = ("README.md", "LICENSE", "CQRSharp_Icon.png")
GENERATOR = "analyzers/dotnet/cs/CQRSharp.Generators.dll"
ANALYZERS = "analyzers/dotnet/cs/CQRSharp.Analyzers.dll"
# Every project that references a CQRSharp package gets the analyzers through it, so they ship in exactly one package:
# two copies of one analyzer assembly in a compilation are redundant at best and version-skewed at worst.
ANALYZERS_PACKAGE = "CQRSharp.Abstractions"

errors: list[str] = []


def fail(message: str) -> None:
    errors.append(message)
    print(f"::error::{message}")


def parse_nuspec(z: zipfile.ZipFile) -> tuple[str, str, list[ElementTree.Element]]:
    """Returns the package id, its version and its <dependency> elements (across every target-framework group)."""
    nuspecs = [n for n in z.namelist() if n.endswith(".nuspec")]
    if len(nuspecs) != 1:
        raise SystemExit(f"Expected 1 .nuspec file, found {len(nuspecs)}: {nuspecs}")
    root = ElementTree.fromstring(z.read(nuspecs[0]))

    def text(name: str) -> str:
        element = root.find(f"{{*}}metadata/{{*}}{name}")
        if element is None or not (element.text or "").strip():
            raise SystemExit(f"Missing nuspec value '{name}' in {nuspecs[0]}")
        return element.text.strip()

    return text("id"), text("version"), root.findall(".//{*}dependencies//{*}dependency")


def normalize_version(v: str) -> str:
    # Enough of NuGet's normalization for the gate: drop +build-metadata and lowercase (prerelease is case-insensitive).
    return v.strip().split("+", 1)[0].lower()


def published_versions(package_id: str) -> set[str]:
    url = f"https://api.nuget.org/v3-flatcontainer/{package_id.lower()}/index.json"
    try:
        with urllib.request.urlopen(url, timeout=30) as resp:
            payload = json.loads(resp.read().decode("utf-8", errors="replace"))
            return {normalize_version(x) for x in payload.get("versions", [])}
    except urllib.error.HTTPError as ex:
        if ex.code == 404:
            return set()
        raise


def check_library(package_id: str, names: set[str], path: str) -> None:
    for tfm in FRAMEWORKS + EXTRA_FRAMEWORKS.get(package_id, ()):
        for ext in ("dll", "xml"):
            entry = f"lib/{tfm}/{package_id}.{ext}"
            if entry not in names:
                fail(f"{package_id}: missing {entry} (is the SDK for {tfm} installed on the runner?)")
    if not os.path.exists(path[: -len(".nupkg")] + ".snupkg"):
        fail(f"{package_id}: no symbol package (.snupkg) next to {os.path.basename(path)}")


def check_analyzers(package_id: str, names: set[str], dependencies: list[ElementTree.Element]) -> None:
    if package_id == ANALYZERS_PACKAGE and ANALYZERS not in names:
        fail(f"{package_id}: missing {ANALYZERS} (the CQRA analyzers ship in this package, and only here).")
    elif package_id != ANALYZERS_PACKAGE and ANALYZERS in names:
        fail(f"{package_id}: embeds {ANALYZERS}; the analyzers ship only in {ANALYZERS_PACKAGE}.")
    # The dependency on another CQRSharp package is how the analyzers reach a project that references this one, so
    # none of them may exclude the Analyzers asset.
    excluding = sorted({
        d.get("id", "") for d in dependencies
        if d.get("id", "").startswith("CQRSharp")
        and "analyzers" in {e.strip().lower() for e in d.get("exclude", "").split(",")}
    })
    for dependency_id in excluding:
        fail(f"{package_id}: its dependency on {dependency_id} excludes Analyzers; the CQRSharp analyzers must flow to "
             f"every project that references {package_id} (reference it with PrivateAssets that keep analyzers).")


def check_pinned(package_id: str, version: str, dependencies: list[ElementTree.Element]) -> None:
    # The CQRSharp packages ship as one family (shared internals, generated code bound to this release), so each one
    # depends on its siblings at exactly its own version; src/Directory.Build.targets writes the range.
    expected = f"[{version}]"
    for d in dependencies:
        dependency_id = d.get("id", "")
        if dependency_id.startswith("CQRSharp") and d.get("version", "") != expected:
            fail(f"{package_id}: depends on {dependency_id} {d.get('version', '')!r}; CQRSharp packages must pin each "
                 f"other to exactly {expected}.")


def check_meta(names: set[str]) -> None:
    if GENERATOR not in names:
        fail(f"{META}: the meta-package must embed {GENERATOR} for plug-and-play.")
    if any(n.startswith("lib/") for n in names):
        fail(f"{META}: the meta-package must not contain lib/ output.")
    if "buildTransitive/CQRSharp.props" not in names:
        fail(f"{META}: missing buildTransitive/CQRSharp.props (the convenience global usings).")


def check_templates(z: zipfile.ZipFile, names: set[str], version: str) -> None:
    if "content/cqrsharp/.template.config/template.json" not in names:
        fail(f"{TEMPLATES}: missing content/cqrsharp/.template.config/template.json")
        return
    project = next((n for n in names if n.startswith("content/cqrsharp/") and n.endswith(".csproj")), None)
    if project is None:
        fail(f"{TEMPLATES}: the template has no project file.")
        return
    pinned = re.search(r'<PackageReference\s+Include="CQRSharp"\s+Version="([^"]+)"', z.read(project).decode("utf-8-sig"))
    if pinned is None:
        fail(f"{TEMPLATES}: {project} does not reference the CQRSharp package.")
    elif normalize_version(pinned.group(1)) != normalize_version(version):
        fail(f"{TEMPLATES}: the template pins CQRSharp {pinned.group(1)} but the packages are {version}.")


def main() -> int:
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    check_nuget = "--check-nuget" in sys.argv
    package_dir = args[0] if args else os.environ.get("PACKAGE_OUTPUT", "artifacts/packages")

    packages = sorted(glob.glob(os.path.join(package_dir, "*.nupkg")))
    if not packages:
        raise SystemExit(f"No .nupkg files found in '{package_dir}'.")

    observed: dict[str, str] = {}
    for path in packages:
        with zipfile.ZipFile(path) as z:
            package_id, version, dependencies = parse_nuspec(z)
            observed[package_id] = version
            names = set(z.namelist())
            check_analyzers(package_id, names, dependencies)
            check_pinned(package_id, version, dependencies)

            for shared in SHARED_FILES:
                if shared not in names:
                    fail(f"{package_id}: missing {shared}")

            if package_id in LIBRARIES:
                check_library(package_id, names, path)
            elif package_id == META:
                check_meta(names)
            elif package_id == TEMPLATES:
                check_templates(z, names, version)

    ids = set(observed)
    if EXPECTED - ids:
        fail(f"Missing expected packages: {sorted(EXPECTED - ids)}")
    if ids - EXPECTED:
        fail(f"Unexpected packages produced: {sorted(ids - EXPECTED)}")

    versions = set(observed.values())
    if len(versions) != 1:
        fail(f"Package versions are not aligned: {sorted(versions)}")

    if errors:
        print(f"{len(errors)} package validation error(s).")
        return 1

    version = next(iter(versions))
    should_publish = False
    if check_nuget:
        existing = sorted(i for i in observed if normalize_version(version) in published_versions(i))
        # Publish only when this version is genuinely new, i.e. at least one package does not have it on NuGet yet.
        # (The push uses --skip-duplicate, so a partially published version is completed rather than rejected.)
        should_publish = len(existing) < len(observed)
        if existing:
            print(f"::warning::Version '{version}' already exists on NuGet for: {', '.join(existing)}")
        if not should_publish:
            print(f"::notice::Version '{version}' is already published for all packages; nothing to publish.")

        gh_output = os.environ.get("GITHUB_OUTPUT")
        if gh_output:
            with open(gh_output, "a", encoding="utf-8") as fh:
                fh.write(f"should_publish={'true' if should_publish else 'false'}\n")
                fh.write(f"package_version={version}\n")

    print(f"Validated {len(packages)} packages, version {version}: {', '.join(sorted(ids))}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
