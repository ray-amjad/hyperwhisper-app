#!/usr/bin/env python3
"""
Compare Next.js i18n JSON message files against the English source of truth.

Identifies missing keys, extra keys, and JSON parse errors in each locale.
Supports nested JSON structures (flattens to dot-separated paths).

Usage:
    python3 compare_messages.py
    python3 compare_messages.py --base nextjs/messages/en.json
    python3 compare_messages.py --base nextjs/messages/en.json --langs nextjs/messages/ja.json
    python3 compare_messages.py --base nextjs/messages/en.json --all
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path


def flatten_keys(d: dict, prefix: str = "") -> set[str]:
    """Recursively flatten nested dict keys into dot-separated paths."""
    keys: set[str] = set()
    for k, v in d.items():
        key = f"{prefix}.{k}" if prefix else k
        if isinstance(v, dict):
            keys.update(flatten_keys(v, key))
        else:
            keys.add(key)
    return keys


def parse_json_keys(path: Path) -> set[str] | None:
    """Parse a JSON file and return flattened keys, or None on error."""
    try:
        with open(path, "r", encoding="utf-8") as f:
            data = json.load(f)
        return flatten_keys(data)
    except json.JSONDecodeError as e:
        print(f"  ERROR: Invalid JSON in {path}: {e}")
        return None
    except Exception as e:
        print(f"  ERROR: Failed to read {path}: {e}")
        return None


def report(label: str, base_keys: set[str], other_keys: set[str]) -> int:
    missing = sorted(base_keys - other_keys)
    extra = sorted(other_keys - base_keys)

    print(f"\n[{label}]")
    print(f"- Base keys: {len(base_keys)}")
    print(f"- {label} keys: {len(other_keys)}")

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
        description="Compare Next.js i18n JSON message files against English"
    )
    parser.add_argument(
        "--base",
        default="nextjs/messages/en.json",
    )
    parser.add_argument(
        "--langs",
        nargs="*",
        help="Specific locale files to check (default: all non-en JSON files)",
    )
    parser.add_argument(
        "--all",
        action="store_true",
        help="Check all locale files in the messages directory",
    )
    args = parser.parse_args()

    base_path = Path(args.base)
    if not base_path.exists():
        print(f"ERROR: Base file not found: {base_path}")
        return 1

    base_keys = parse_json_keys(base_path)
    if base_keys is None or not base_keys:
        print(f"No keys found in base file: {base_path}")
        return 1

    print(f"Base: {base_path} ({len(base_keys)} keys)")

    # Determine which locale files to check
    if args.langs:
        lang_paths = [Path(p) for p in args.langs]
    else:
        # Auto-discover all non-en JSON files in the same directory
        messages_dir = base_path.parent
        lang_paths = sorted(
            p for p in messages_dir.glob("*.json")
            if p.name != base_path.name and p.name != "CLAUDE.md"
        )

    if not lang_paths:
        print("No locale files found to compare.")
        return 0

    total_issues = 0
    synced_count = 0
    error_count = 0

    for lang_path in lang_paths:
        label = lang_path.stem  # e.g., "ja", "zh-Hant"
        if not lang_path.exists():
            print(f"\n[{label}]\n- Missing file: {lang_path}")
            total_issues += 1
            error_count += 1
            continue

        other_keys = parse_json_keys(lang_path)
        if other_keys is None:
            total_issues += 1
            error_count += 1
            continue

        issues = report(label, base_keys, other_keys)
        total_issues += issues
        if issues == 0:
            synced_count += 1

    print(f"\n{'=' * 40}")
    print(f"Total locales checked: {len(lang_paths)}")
    print(f"In sync: {synced_count}")
    print(f"With issues: {len(lang_paths) - synced_count - error_count}")
    if error_count:
        print(f"Errors: {error_count}")
    print(f"Total issues: {total_issues}")
    return 0 if total_issues == 0 else 2


if __name__ == "__main__":
    raise SystemExit(main())
