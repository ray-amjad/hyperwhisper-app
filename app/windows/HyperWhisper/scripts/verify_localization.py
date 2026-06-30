#!/usr/bin/env python3
"""
Localization Verification Script

Compares all localized .resx files against the base English file (Strings.resx)
to ensure all translations are in sync.

Usage:
    python verify_localization.py
"""

import os
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def parse_resx_keys(file_path: Path) -> set[str]:
    """Extract all data keys from a .resx file."""
    keys = set()
    try:
        tree = ET.parse(file_path)
        root = tree.getroot()
        for data in root.findall('data'):
            name = data.get('name')
            if name:
                keys.add(name)
    except ET.ParseError as e:
        print(f"  ERROR: Failed to parse {file_path.name}: {e}")
        return set()
    return keys


def main():
    # Find the Resources directory
    script_dir = Path(__file__).parent
    resources_dir = script_dir.parent / "Resources"

    if not resources_dir.exists():
        print(f"ERROR: Resources directory not found at {resources_dir}")
        sys.exit(1)

    # Define file paths
    base_file = resources_dir / "Strings.resx"
    localized_files = {
        "Spanish (es)": resources_dir / "Strings.es.resx",
        "Japanese (ja)": resources_dir / "Strings.ja.resx",
        "Chinese Simplified (zh-Hans)": resources_dir / "Strings.zh-Hans.resx",
    }

    # Verify base file exists
    if not base_file.exists():
        print(f"ERROR: Base file not found: {base_file}")
        sys.exit(1)

    print("=" * 60)
    print("Localization Verification Report")
    print("=" * 60)
    print()

    # Parse base file
    print(f"Base file: {base_file.name}")
    base_keys = parse_resx_keys(base_file)
    print(f"  Total keys: {len(base_keys)}")
    print()

    # Track overall status
    all_synced = True

    # Check each localized file
    for lang_name, file_path in localized_files.items():
        print("-" * 60)
        print(f"{lang_name}: {file_path.name}")

        if not file_path.exists():
            print(f"  ERROR: File not found!")
            all_synced = False
            continue

        lang_keys = parse_resx_keys(file_path)
        print(f"  Total keys: {len(lang_keys)}")

        # Find missing keys (in base but not in translation)
        missing_keys = base_keys - lang_keys
        if missing_keys:
            all_synced = False
            print(f"  MISSING ({len(missing_keys)} keys):")
            for key in sorted(missing_keys):
                print(f"    - {key}")

        # Find extra keys (in translation but not in base)
        extra_keys = lang_keys - base_keys
        if extra_keys:
            all_synced = False
            print(f"  EXTRA ({len(extra_keys)} keys - should not exist):")
            for key in sorted(extra_keys):
                print(f"    - {key}")

        if not missing_keys and not extra_keys:
            print("  Status: IN SYNC")

    print()
    print("=" * 60)
    if all_synced:
        print("RESULT: All localization files are in sync!")
        print("=" * 60)
        sys.exit(0)
    else:
        print("RESULT: Localization files have discrepancies!")
        print("=" * 60)
        sys.exit(1)


if __name__ == "__main__":
    main()
