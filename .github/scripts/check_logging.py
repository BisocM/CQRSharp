#!/usr/bin/env python3
"""Checks the logging contract of the shipped libraries (src/).

Every log line a CQRSharp library writes is a [LoggerMessage] source-generated method with its own event id. That keeps
the event ids and property names a stable contract operators can filter and alert on, and it keeps disabled log levels
free: a generated method checks IsEnabled before it touches its arguments, while the ILogger.Log*(...) extension methods
allocate their params array and box value types first, on every call.

Fails when a file under src/ calls an ILogger.Log*(...) extension method (or ILogger.Log(...)) directly, when a
[LoggerMessage] declares no event id, or when two [LoggerMessage] attributes of one assembly (one src/<project>/ folder)
declare the same event id. Ids are scoped by assembly, not repo-wide: each component logs under its own category and
owns a block of ids (docs/observability.md lists them).

Usage: check_logging.py [repo-root]    (defaults to the repository this script lives in)
"""

import re
import sys
from collections import defaultdict
from pathlib import Path

# A direct call of the extension methods (or of ILogger.Log itself): `.LogWarning(`, `.Log(`, ... on any receiver but
# Math/MathF (whose Log is the logarithm). The generated methods are called unqualified or through their own log class,
# and are never named exactly like these.
DIRECT_CALL = re.compile(
    r"(?<!\bMath)(?<!\bMathF)\.\s*Log(?:Trace|Debug|Information|Warning|Error|Critical)?\s*(?:<[^>()]*>)?\s*\(")
ATTRIBUTE = re.compile(r"\[\s*(?:Microsoft\.Extensions\.Logging\.)?LoggerMessage(?:Attribute)?\s*\(")


def strip_comments_and_strings(source: str) -> str:
    """Blanks out comments and string/char literals, keeping every character's position (line breaks stay), so matches
    only ever hit code and an index into the result is an index into the source."""
    out = []
    i, n = 0, len(source)
    while i < n:
        c = source[i]
        nxt = source[i + 1] if i + 1 < n else ""
        if c == "/" and nxt == "/":
            end = source.find("\n", i)
            end = n if end == -1 else end
            out.append(" " * (end - i))
            i = end
        elif c == "/" and nxt == "*":
            end = source.find("*/", i + 2)
            end = n if end == -1 else end + 2
            out.append(re.sub(r"[^\n]", " ", source[i:end]))
            i = end
        elif c in "\"'" or (c in "@$" and nxt in "\"@$"):
            end = skip_literal(source, i)
            out.append(re.sub(r"[^\n]", " ", source[i:end]))
            i = end
        else:
            out.append(c)
            i += 1
    return "".join(out)


def skip_literal(source: str, start: int) -> int:
    """Returns the index just past the string or char literal that starts at `start`."""
    i = start
    verbatim = False
    while source[i] in "@$":
        verbatim |= source[i] == "@"
        i += 1
    quote = source[i]
    if source.startswith('"""', i):  # raw string literal
        run = len(source[i:]) - len(source[i:].lstrip('"'))
        end = source.find('"' * run, i + run)
        return len(source) if end == -1 else end + run
    i += 1
    while i < len(source):
        ch = source[i]
        if verbatim and ch == '"' and source.startswith('""', i):
            i += 2
            continue
        if not verbatim and ch == "\\":
            i += 2
            continue
        if ch == quote:
            return i + 1
        if ch == "\n" and not verbatim:
            return i
        i += 1
    return i


def attribute_arguments(raw: str, code: str, start: int) -> str:
    """The source text between the attribute's parentheses; `start` points just past its `(`. The parentheses are
    matched in `code`, where string literals are blanked, so a parenthesis inside the message template cannot count."""
    depth, i = 1, start
    while i < len(code) and depth:
        if code[i] == "(":
            depth += 1
        elif code[i] == ")":
            depth -= 1
        i += 1
    return raw[start:i - 1]


def event_id_of(arguments: str):
    named = re.search(r"\bEventId\s*=\s*(\d+)", arguments)
    if named:
        return int(named.group(1))
    first = arguments.split(",", 1)[0].strip()
    return int(first) if first.isdigit() else None


def main() -> int:
    root = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
    src = root / "src"
    if not src.is_dir():
        print(f"error: no src/ folder under {root}")
        return 1

    problems = []
    ids = defaultdict(list)  # (assembly, id) -> [location]
    files = 0
    messages = 0

    for path in sorted(src.rglob("*.cs")):
        relative = path.relative_to(root)
        if {"obj", "bin"} & set(relative.parts):
            continue
        files += 1
        raw = path.read_text(encoding="utf-8-sig")
        code = strip_comments_and_strings(raw)
        assembly = relative.parts[1]

        for match in DIRECT_CALL.finditer(code):
            line = code.count("\n", 0, match.start()) + 1
            problems.append(f"{relative}:{line}: logs through an ILogger extension method; declare a [LoggerMessage] method instead")

        for match in ATTRIBUTE.finditer(code):
            line = code.count("\n", 0, match.start()) + 1
            event_id = event_id_of(attribute_arguments(raw, code, match.end()))
            messages += 1
            if event_id is None or event_id == 0:
                problems.append(f"{relative}:{line}: [LoggerMessage] declares no event id")
                continue
            ids[(assembly, event_id)].append(f"{relative}:{line}")

    for (assembly, event_id), locations in sorted(ids.items()):
        if len(locations) > 1:
            problems.append(f"{assembly}: event id {event_id} is declared {len(locations)} times: {', '.join(locations)}")

    if problems:
        print("The logging contract is broken:")
        for problem in problems:
            print(f"  {problem}")
        return 1

    print(f"Logging contract holds: {messages} [LoggerMessage] methods with distinct event ids, no direct ILogger calls, in {files} files.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
