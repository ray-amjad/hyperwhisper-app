#!/usr/bin/env python3
"""
Find localization keys in the macOS Base.lproj/Localizable.strings that are
not referenced anywhere in the Swift source tree.

Scans app/macos/hyperwhisper/ for .swift files (excluding Libraries/, which
contains third-party code like Sentry). For each key, looks for the literal
string "key.name" in any .swift file. Also identifies keys whose prefix
matches a string-interpolation site like "foo.bar.\\(mode)" — those are
flagged as "maybe-used" since they may be assembled at runtime.

Usage:
    python3 find_unused_keys.py
    python3 find_unused_keys.py --source-root app/macos/hyperwhisper
    python3 find_unused_keys.py --json out.json

Exit codes:
    0 — no definitely-unused keys found
    2 — definitely-unused keys present (review the report)
"""
from __future__ import annotations

import argparse
import json
import re
from pathlib import Path

KEY_RE = re.compile(r'^\s*"([^"]+)"\s*=')
# Matches a string literal that contains interpolation, capturing the
# static prefix up to the first `\(`. Example: "settings.mode.\(x)" -> "settings.mode."
INTERP_RE = re.compile(r'"([^"\\]*?)\\\(')

EXCLUDE_DIRS = {"Libraries", "Pods", ".build", "build", "DerivedData"}


def parse_keys(path: Path) -> list[str]:
    keys: list[str] = []
    with path.open("r", encoding="utf-8") as f:
        for line in f:
            m = KEY_RE.match(line)
            if m:
                keys.append(m.group(1))
    return keys


def iter_swift_files(root: Path):
    for p in root.rglob("*.swift"):
        # Skip excluded directories anywhere in the path
        if any(part in EXCLUDE_DIRS for part in p.parts):
            continue
        yield p


def load_sources(root: Path) -> tuple[str, list[str]]:
    """Return (concatenated-source, list-of-interpolation-prefixes)."""
    chunks: list[str] = []
    interp_prefixes: set[str] = set()
    for path in iter_swift_files(root):
        try:
            text = path.read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        chunks.append(text)
        for m in INTERP_RE.finditer(text):
            prefix = m.group(1)
            if prefix:
                interp_prefixes.add(prefix)
    return "\n".join(chunks), sorted(interp_prefixes)


def classify(
    keys: list[str], haystack: str, interp_prefixes: list[str]
) -> tuple[list[str], list[tuple[str, str]], list[str]]:
    used: list[str] = []
    maybe: list[tuple[str, str]] = []  # (key, matching-prefix)
    unused: list[str] = []

    for key in keys:
        literal = f'"{key}"'
        if literal in haystack:
            used.append(key)
            continue
        match_prefix = None
        for prefix in interp_prefixes:
            if key.startswith(prefix):
                match_prefix = prefix
                break
        if match_prefix is not None:
            maybe.append((key, match_prefix))
        else:
            unused.append(key)

    return used, maybe, unused


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Find unused localization keys in macOS Base.lproj"
    )
    parser.add_argument(
        "--base",
        default="app/macos/hyperwhisper/Localizations/Base.lproj/Localizable.strings",
    )
    parser.add_argument(
        "--source-root",
        default="app/macos/hyperwhisper",
        help="Root directory to scan for .swift files",
    )
    parser.add_argument(
        "--json",
        default=None,
        help="Write the full report as JSON to this path",
    )
    args = parser.parse_args()

    base_path = Path(args.base)
    src_root = Path(args.source_root)

    if not base_path.exists():
        print(f"ERROR: Base file not found: {base_path}")
        return 1
    if not src_root.exists():
        print(f"ERROR: Source root not found: {src_root}")
        return 1

    keys = parse_keys(base_path)
    if not keys:
        print(f"No keys found in base file: {base_path}")
        return 1

    print(f"Base file: {base_path} ({len(keys)} keys)")
    print(f"Scanning: {src_root}")

    haystack, interp_prefixes = load_sources(src_root)
    print(f"Found {len(interp_prefixes)} string-interpolation prefixes")

    used, maybe, unused = classify(keys, haystack, interp_prefixes)

    print()
    print(f"Used:              {len(used)}")
    print(f"Maybe-used (dyn):  {len(maybe)}")
    print(f"Definitely unused: {len(unused)}")

    if maybe:
        print("\n[maybe-unused — matches an interpolation prefix]")
        for key, prefix in maybe:
            print(f'  - {key}   (prefix "{prefix}")')

    if unused:
        print("\n[definitely-unused — no literal match in source]")
        for key in unused:
            print(f"  - {key}")

    if args.json:
        Path(args.json).write_text(
            json.dumps(
                {
                    "base": str(base_path),
                    "source_root": str(src_root),
                    "total": len(keys),
                    "used": used,
                    "maybe_unused": [
                        {"key": k, "prefix": p} for k, p in maybe
                    ],
                    "unused": unused,
                },
                indent=2,
            ),
            encoding="utf-8",
        )
        print(f"\nJSON report written to {args.json}")

    return 0 if not unused else 2


if __name__ == "__main__":
    raise SystemExit(main())
