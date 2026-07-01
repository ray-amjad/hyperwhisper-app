#!/usr/bin/env python3
"""
Compare Windows .resx localization files against the base English file.

Identifies missing keys, extra keys, and duplicate keys in each locale.

Usage:
    python3 compare_resx.py
    python3 compare_resx.py --base path/to/Strings.resx --langs path/to/Strings.ja.resx
"""
from __future__ import annotations

import argparse
import xml.etree.ElementTree as ET
from pathlib import Path


def parse_resx_keys(path: Path) -> list[str]:
    """Extract all data keys from a .resx file, preserving order and duplicates."""
    keys: list[str] = []
    try:
        tree = ET.parse(path)
        root = tree.getroot()
        for data in root.findall("data"):
            name = data.get("name")
            if name:
                keys.append(name)
    except ET.ParseError as e:
        print(f"  ERROR: Failed to parse {path}: {e}")
    return keys


def report(label: str, base_keys: list[str], other_keys: list[str]) -> int:
    base_set = set(base_keys)
    other_set = set(other_keys)
    missing = sorted(base_set - other_set)
    extra = sorted(other_set - base_set)

    print(f"\n[{label}]")
    print(f"- Base keys: {len(base_set)}")
    print(f"- {label} keys: {len(other_set)}")

    dupes = [k for k in other_keys if other_keys.count(k) > 1]
    if dupes:
        unique_dupes = sorted(set(dupes))
        print(f"- Duplicate keys: {len(unique_dupes)}")
        for key in unique_dupes:
            print(f"  - {key}")
    else:
        print("- Duplicate keys: 0")

    if missing:
        print(f"- Missing keys: {len(missing)}")
        for key in missing:
            print(f"  - {key}")
    else:
        print("- Missing keys: 0")

    if extra:
        print(f"- Extra keys: {len(extra)}")
        for key in extra:
            print(f"  - {key}")
    else:
        print("- Extra keys: 0")

    return len(missing) + len(extra)


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Compare Windows .resx localization files against the base"
    )
    parser.add_argument(
        "--base",
        default="app/windows/HyperWhisper/Resources/Strings.resx",
    )
    parser.add_argument(
        "--langs",
        nargs="*",
        default=None,
        help="Locale files to check. If omitted, auto-discovers all Strings.*.resx files.",
    )
    args = parser.parse_args()

    base_path = Path(args.base)
    if not base_path.exists():
        print(f"ERROR: Base file not found: {base_path}")
        return 1

    base_keys = parse_resx_keys(base_path)
    if not base_keys:
        print(f"No keys found in base file: {base_path}")
        return 1

    # Auto-discover all locale files if none specified
    lang_paths = args.langs
    if lang_paths is None:
        resource_dir = base_path.parent
        lang_paths = sorted(
            str(p) for p in resource_dir.glob("Strings.*.resx")
        )
        if not lang_paths:
            print(f"No locale files found in {resource_dir}")
            return 1

    print(f"Base: {base_path} ({len(set(base_keys))} keys)")
    print(f"Checking {len(lang_paths)} locale file(s)")

    total_issues = 0
    for lang_path_str in lang_paths:
        lang_path = Path(lang_path_str)
        # Extract locale from filename: Strings.ja.resx -> ja
        label = lang_path.stem.replace("Strings.", "").replace("Strings", "base")
        if not lang_path.exists():
            print(f"\n[{label}]\n- Missing file: {lang_path}")
            total_issues += 1
            continue
        other_keys = parse_resx_keys(lang_path)
        total_issues += report(label, base_keys, other_keys)

    print(f"\nTotal issues: {total_issues}")
    return 0 if total_issues == 0 else 2


if __name__ == "__main__":
    raise SystemExit(main())
