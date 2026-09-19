#!/usr/bin/env python3
"""Validates the packed CQRSharp packages. Shared by CI (structure only) and the release workflow (--check-nuget).

    python3 .github/scripts/validate_packages.py <package-dir> [--check-nuget]

Structure: the exact expected set of packages, one aligned version, every library built for every target framework with
its XML docs and a symbol package, the meta-package's embedded generator/analyzers, the template's pinned version, and
the shared README / license / icon in each package.

--check-nuget additionally asks nuget.org which packages already have this version and writes `should_publish` and
`package_version` to $GITHUB_OUTPUT, so a push to Release that did not change the version publishes nothing.
"""
from __future__ import annotations

import glob
import json
import os
import re
import sys
import urllib.error
import urllib.request
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
}
EXPECTED = LIBRARIES | {META, TEMPLATES}
FRAMEWORKS = ("net8.0", "net9.0", "net10.0")
SHARED_FILES = ("README.md", "LICENSE", "CQRSharp_Icon.png")

errors: list[str] = []


def fail(message: str) -> None:
    errors.append(message)
    print(f"::error::{message}")


def parse_nuspec(z: zipfile.ZipFile) -> tuple[str, str]:
    nuspecs = [n for n in z.namelist() if n.endswith(".nuspec")]
    if len(nuspecs) != 1:
        raise SystemExit(f"Expected 1 .nuspec file, found {len(nuspecs)}: {nuspecs}")
    nuspec = z.read(nuspecs[0]).decode("utf-8-sig", errors="replace")

    def text(name: str) -> str:
        match = re.search(rf"<{name}>\s*([^<]+?)\s*</{name}>", nuspec)
        if match is None:
            raise SystemExit(f"Missing nuspec value '{name}' in {nuspecs[0]}")
        return match.group(1)

    return text("id"), text("version")


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
    for tfm in FRAMEWORKS:
        for ext in ("dll", "xml"):
            entry = f"lib/{tfm}/{package_id}.{ext}"
            if entry not in names:
                fail(f"{package_id}: missing {entry} (is the SDK for {tfm} installed on the runner?)")
    if not os.path.exists(path[: -len(".nupkg")] + ".snupkg"):
        fail(f"{package_id}: no symbol package (.snupkg) next to {os.path.basename(path)}")


def check_meta(names: set[str]) -> None:
    for assembly in ("CQRSharp.Generators.dll", "CQRSharp.Analyzers.dll"):
        if f"analyzers/dotnet/cs/{assembly}" not in names:
            fail(f"{META}: the meta-package must embed analyzers/dotnet/cs/{assembly} for plug-and-play.")
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
            package_id, version = parse_nuspec(z)
            observed[package_id] = version
            names = set(z.namelist())

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
