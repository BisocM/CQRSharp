#!/usr/bin/env python3
"""Prints one version's CHANGELOG.md section: the body of that version's GitHub release.

    python3 .github/scripts/release_notes.py <version> [<changelog>]

The section is everything under the `## [<version>]` heading up to the next `## [` heading. The release workflow runs
this before it publishes, so a version without a changelog section fails while nothing has been pushed yet, and again
after the push to create the release. Exits non-zero when the section is missing or empty.
"""
from __future__ import annotations

import os
import re
import sys

# GitHub rejects a release body longer than this many characters.
MAX_BODY = 125_000


def section(changelog: str, version: str) -> str | None:
    heading = re.compile(rf"^## \[{re.escape(version)}\][^\n]*$", re.M)
    start = heading.search(changelog)
    if start is None:
        return None
    following = re.compile(r"^## \[", re.M).search(changelog, start.end())
    body = changelog[start.end(): following.start() if following else len(changelog)]
    return body.strip()


def fit(body: str, version: str) -> str:
    if len(body) <= MAX_BODY:
        return body
    repository = os.environ.get("GITHUB_REPOSITORY", "BisocM/CQRSharp")
    footer = (
        f"\n\n_The notes continue in the [changelog](https://github.com/{repository}/blob/v{version}/CHANGELOG.md); "
        f"they are longer than a GitHub release can hold._"
    )
    cut = body.rfind("\n### ", 0, MAX_BODY - len(footer))
    if cut <= 0:
        cut = body.rfind("\n", 0, MAX_BODY - len(footer))
    return body[:cut].rstrip() + footer


def main() -> int:
    if len(sys.argv) not in (2, 3):
        print(__doc__, file=sys.stderr)
        return 2
    version = sys.argv[1].strip()
    path = sys.argv[2] if len(sys.argv) == 3 else "CHANGELOG.md"
    with open(path, encoding="utf-8") as fh:
        body = section(fh.read(), version)
    if not body:
        print(f"::error::{path} has no '## [{version}]' section (or it is empty): write the release notes before "
              f"publishing {version}.", file=sys.stderr)
        return 1
    sys.stdout.write(fit(body, version) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
