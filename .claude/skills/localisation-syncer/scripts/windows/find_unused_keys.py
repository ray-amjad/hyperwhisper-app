#!/usr/bin/env python3
"""
Find localization keys in the Windows base Strings.resx that are not
referenced anywhere in the source tree.

HyperWhisper Windows uses a `Loc` helper (Localization/Loc.cs) that looks
up strings by their literal dotted key at runtime:
    C#:   Loc.S("sidebar.home")
    XAML: {loc:Loc sidebar.home}

So we scan every .cs / .xaml under app/windows/HyperWhisper/ and look for
each key in two forms:
    1. As a double-quoted literal:  "sidebar.home"
    2. As a bare XAML token after  `loc:Loc ` / `Loc `:  sidebar.home

Keys whose prefix matches a C# interpolated string of the form $"foo.{x}"
are flagged "maybe-used" rather than unused.

Usage:
    python3 find_unused_keys.py
    python3 find_unused_keys.py --source-root app/windows/HyperWhisper
    python3 find_unused_keys.py --json out.json

Exit codes:
    0 — no definitely-unused keys found
    2 — definitely-unused keys present (review the report)
"""
from __future__ import annotations

import argparse
import json
import re
import xml.etree.ElementTree as ET
from pathlib import Path

# $"prefix.{arg}" or $"prefix.{arg}.suffix"
CSHARP_INTERP_RE = re.compile(r'\$"([^"{}\\]*?)\{')
# {loc:Loc key.name} or {loc:Loc key.name, ...}
XAML_LOC_RE = re.compile(r"\{(?:loc:)?Loc\s+([^\s,}]+)")

EXCLUDE_DIRS = {"bin", "obj", ".vs", "packages", "Migrations"}


def parse_resx_keys(path: Path) -> list[str]:
    keys: list[str] = []
    tree = ET.parse(path)
    root = tree.getroot()
    for data in root.findall("data"):
        name = data.get("name")
        if name:
            keys.append(name)
    return keys


def iter_source_files(root: Path):
    for pattern in ("*.cs", "*.xaml"):
        for p in root.rglob(pattern):
            if any(part in EXCLUDE_DIRS for part in p.parts):
                continue
            # Skip auto-generated designer files — they only reflect the resx,
            # not actual usage.
            if p.name.endswith(".Designer.cs") or p.name.endswith(".g.cs"):
                continue
            yield p


def load_sources(root: Path):
    chunks: list[str] = []
    interp_prefixes: set[str] = set()
    xaml_loc_keys: set[str] = set()

    for path in iter_source_files(root):
        try:
            text = path.read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        chunks.append(text)
        if path.suffix == ".cs":
            for m in CSHARP_INTERP_RE.finditer(text):
                prefix = m.group(1)
                if prefix:
                    interp_prefixes.add(prefix)
        elif path.suffix == ".xaml":
            for m in XAML_LOC_RE.finditer(text):
                xaml_loc_keys.add(m.group(1).strip())

    return "\n".join(chunks), sorted(interp_prefixes), xaml_loc_keys


def classify(
    keys: list[str],
    haystack: str,
    interp_prefixes: list[str],
    xaml_loc_keys: set[str],
):
    used: list[str] = []
    maybe: list[tuple[str, str]] = []
    unused: list[str] = []

    for key in keys:
        if f'"{key}"' in haystack or key in xaml_loc_keys:
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
        description="Find unused localization keys in Windows Strings.resx"
    )
    parser.add_argument(
        "--base",
        default="app/windows/HyperWhisper/Resources/Strings.resx",
    )
    parser.add_argument(
        "--source-root",
        default="app/windows/HyperWhisper",
        help="Root directory to scan for .cs and .xaml files",
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

    keys = parse_resx_keys(base_path)
    if not keys:
        print(f"No keys found in base file: {base_path}")
        return 1

    print(f"Base file: {base_path} ({len(keys)} keys)")
    print(f"Scanning: {src_root}")

    haystack, interp_prefixes, xaml_loc_keys = load_sources(src_root)
    print(
        f"Found {len(interp_prefixes)} C# interp prefixes, "
        f"{len(xaml_loc_keys)} XAML {{loc:Loc ...}} references"
    )

    used, maybe, unused = classify(keys, haystack, interp_prefixes, xaml_loc_keys)

    print()
    print(f"Used:              {len(used)}")
    print(f"Maybe-used (dyn):  {len(maybe)}")
    print(f"Definitely unused: {len(unused)}")

    if maybe:
        print("\n[maybe-unused — matches a C# interpolation prefix]")
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
