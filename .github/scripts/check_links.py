#!/usr/bin/env python3
"""Checks the links between the repository's Markdown files.

Every Markdown file git tracks links to the others by relative path, which GitHub resolves on any branch or tag (the
packed README has its links pinned to the version's tag at pack time; validate_packages.py checks that copy). This fails
on a relative link to a file or folder that does not exist, and on a #fragment that names no heading of its target (or
of the same file). Links inside code blocks and inline code are not links, and URLs with a scheme are not checked.

Usage: check_links.py [repo-root]    (defaults to the repository this script lives in)
"""

import html
import re
import subprocess
import sys
from collections import Counter
from pathlib import Path
from urllib.parse import unquote

FENCE = re.compile(r"^ {0,3}(```|~~~).*?^ {0,3}\1[^\n]*$", re.M | re.S)
INLINE_CODE = re.compile(r"(`+)(?:(?!\1).)+?\1", re.S)
# An inline link or image target, with an optional "title".
LINK = re.compile(r"\]\(\s*<?([^)\s>]+)>?(?:\s+\"[^\"]*\")?\s*\)")
HEADING = re.compile(r"^ {0,3}#{1,6}\s+(.*?)\s*#*\s*$", re.M)
HTML_ANCHOR = re.compile(r"<a\s+(?:name|id)=\"([^\"]+)\"", re.I)
SCHEME = re.compile(r"^[A-Za-z][A-Za-z0-9+.\-]*:")


def strip_code(text: str) -> str:
    return INLINE_CODE.sub("", FENCE.sub("", text))


def slug(heading: str) -> str:
    # GitHub's rule, on the heading as rendered (markup gone, entities decoded): drop punctuation (keeping letters,
    # digits, '-', '_' and spaces), lower-case, and turn each space into a '-'.
    text = re.sub(r"!?\[([^\]]*)\]\([^)]*\)", r"\1", heading)
    text = html.unescape(re.sub(r"<[^>]+>", "", text).replace("`", ""))
    return re.sub(r"[^\w\- ]", "", text.lower()).replace(" ", "-")


def anchors(path: Path, cache: dict[Path, set[str]]) -> set[str]:
    if path not in cache:
        text = FENCE.sub("", path.read_text(encoding="utf-8"))
        seen: Counter[str] = Counter()
        found = set(HTML_ANCHOR.findall(text))
        for heading in HEADING.findall(text):
            base = slug(heading)
            found.add(base if seen[base] == 0 else f"{base}-{seen[base]}")
            seen[base] += 1
        cache[path] = found
    return cache[path]


def main() -> int:
    root = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
    tracked = subprocess.run(["git", "ls-files", "*.md"], cwd=root, check=True, capture_output=True, text=True)
    files = [root / name for name in tracked.stdout.splitlines()]

    cache: dict[Path, set[str]] = {}
    errors = 0
    for source in files:
        for target in LINK.findall(strip_code(source.read_text(encoding="utf-8"))):
            if SCHEME.match(target):
                continue
            path_part, _, fragment = target.partition("#")
            destination = (source.parent / unquote(path_part)).resolve() if path_part else source
            where = source.relative_to(root)
            if path_part.startswith("/") or not destination.exists():
                print(f"::error file={where}::{where} links to {target!r}, which does not exist.")
                errors += 1
            elif fragment and destination.suffix == ".md" and unquote(fragment) not in anchors(destination, cache):
                print(f"::error file={where}::{where} links to {target!r}, but no heading there has that anchor.")
                errors += 1

    if errors:
        print(f"{errors} broken link(s).")
        return 1
    print(f"Checked the links of {len(files)} Markdown files.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
