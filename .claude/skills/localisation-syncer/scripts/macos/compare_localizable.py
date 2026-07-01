#!/usr/bin/env python3
from __future__ import annotations

import argparse
import re
from pathlib import Path

KEY_RE = re.compile(r'^\s*"([^"]+)"\s*=')


def parse_keys(path: Path) -> list[str]:
    keys: list[str] = []
    with path.open("r", encoding="utf-8") as f:
        for line in f:
            match = KEY_RE.match(line)
            if match:
                keys.append(match.group(1))
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


def discover_locale_files(base_path: Path) -> list[Path]:
    """Auto-discover all non-Base locale files in the Localizations directory."""
    loc_dir = base_path.parent.parent  # Go from Base.lproj/ up to Localizations/
    locale_files = sorted(
        p
        for p in loc_dir.glob("*.lproj/Localizable.strings")
        if p.parent.name != "Base.lproj"
    )
    return locale_files


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Compare Localizable.strings keys against Base.lproj"
    )
    parser.add_argument(
        "--base",
        default="app/macos/hyperwhisper/Localizations/Base.lproj/Localizable.strings",
    )
    parser.add_argument(
        "--langs",
        nargs="*",
        help="Locale files to check (default: auto-discover all non-Base locales)",
    )
    args = parser.parse_args()

    base_path = Path(args.base)
    base_keys = parse_keys(base_path)

    if not base_keys:
        print(f"No keys found in base file: {base_path}")
        return 1

    # Auto-discover all locale files if none specified
    if args.langs:
        lang_paths = [Path(p) for p in args.langs]
    else:
        lang_paths = discover_locale_files(base_path)
        if not lang_paths:
            print("No locale files found!")
            return 1
        print(f"Discovered {len(lang_paths)} locale files")

    total_issues = 0
    for lang_path in lang_paths:
        label = lang_path.parent.name
        if not lang_path.exists():
            print(f"\n[{label}]\n- Missing file: {lang_path}")
            total_issues += 1
            continue
        other_keys = parse_keys(lang_path)
        total_issues += report(label, base_keys, other_keys)

    print(f"\nTotal issues: {total_issues}")
    return 0 if total_issues == 0 else 2


if __name__ == "__main__":
    raise SystemExit(main())
