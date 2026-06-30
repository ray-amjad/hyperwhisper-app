#!/usr/bin/env python3
"""
Verify shared preset descriptions against macOS and Windows literals.

Usage:
    python3 tools/verify-preset-descriptions.py
"""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path


PRESET_IDS = ("hyper", "message", "mail", "note", "meeting", "code", "custom")
REQUIRED_FIELDS = ("displayName", "tooltip", "preview", "description")


def decode_string_literal(value: str) -> str:
    return json.loads(f'"{value}"')


def load_json(root: Path) -> dict[str, dict[str, str]]:
    path = root / "shared-types" / "preset-descriptions.json"
    with path.open("r", encoding="utf-8") as handle:
        data = json.load(handle)

    presets = data.get("presets")
    if not isinstance(presets, dict):
        raise ValueError("preset-descriptions.json must contain a presets object")

    missing = [preset for preset in PRESET_IDS if preset not in presets]
    if missing:
        raise ValueError(f"Missing presets: {', '.join(missing)}")

    for preset_id in PRESET_IDS:
        preset = presets[preset_id]
        missing_fields = [field for field in REQUIRED_FIELDS if not preset.get(field)]
        if missing_fields:
            raise ValueError(
                f"{preset_id} is missing fields: {', '.join(missing_fields)}"
            )

    return presets


def load_macos_strings(root: Path) -> dict[str, str]:
    path = (
        root
        / "app"
        / "macos"
        / "hyperwhisper"
        / "Localizations"
        / "Base.lproj"
        / "Localizable.strings"
    )
    strings: dict[str, str] = {}
    pattern = re.compile(r'^"(?P<key>[^"]+)"\s*=\s*"(?P<value>(?:[^"\\]|\\.)*)";')

    for line in path.read_text(encoding="utf-8").splitlines():
        match = pattern.match(line)
        if match:
            strings[match.group("key")] = decode_string_literal(match.group("value"))

    return strings


def extract_switch_method(source: str, method_name: str) -> dict[str, str]:
    method_pattern = re.compile(
        rf"public static string {method_name}\(this PresetType preset\)\s*=>\s*preset switch\s*"
        r"\{(?P<body>.*?)\n\s*\};",
        re.DOTALL,
    )
    match = method_pattern.search(source)
    if not match:
        raise ValueError(f"Could not find PresetTypeExtensions.{method_name}")

    entries: dict[str, str] = {}
    entry_pattern = re.compile(
        r'PresetType\.(?P<name>[A-Za-z]+)\s*=>\s*"(?P<value>(?:[^"\\]|\\.)*)"'
    )
    for entry in entry_pattern.finditer(match.group("body")):
        preset_id = entry.group("name")[0].lower() + entry.group("name")[1:]
        entries[preset_id] = decode_string_literal(entry.group("value"))

    return entries


def load_windows_preset_methods(root: Path) -> tuple[dict[str, str], dict[str, str]]:
    path = root / "app" / "windows" / "HyperWhisper" / "Models" / "PresetType.cs"
    source = path.read_text(encoding="utf-8")
    return (
        extract_switch_method(source, "ToDisplayName"),
        extract_switch_method(source, "ToDescription"),
    )


def compare(label: str, expected: str, actual: str | None, errors: list[str]) -> None:
    if actual != expected:
        errors.append(f"{label}: expected {expected!r}, found {actual!r}")


def main() -> int:
    root = Path(__file__).resolve().parents[1]

    try:
        presets = load_json(root)
        macos_strings = load_macos_strings(root)
        windows_display_names, windows_descriptions = load_windows_preset_methods(root)
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(f"ERROR: {error}")
        return 1

    errors: list[str] = []

    for preset_id in PRESET_IDS:
        preset = presets[preset_id]
        compare(
            f"macOS {preset_id} displayName",
            preset["displayName"],
            macos_strings.get(f"modes.preset.{preset_id}.name"),
            errors,
        )
        compare(
            f"macOS {preset_id} tooltip",
            preset["tooltip"],
            macos_strings.get(f"modes.preset.{preset_id}.tooltip"),
            errors,
        )
        compare(
            f"macOS {preset_id} preview",
            preset["preview"],
            macos_strings.get(f"modes.preset.{preset_id}.preview"),
            errors,
        )
        compare(
            f"Windows {preset_id} displayName",
            preset.get("windowsDisplayName", preset["displayName"]),
            windows_display_names.get(preset_id),
            errors,
        )
        compare(
            f"Windows {preset_id} description",
            preset["description"],
            windows_descriptions.get(preset_id),
            errors,
        )

    if errors:
        print("Preset description verification failed:")
        for error in errors:
            print(f"  - {error}")
        print("Run `python3 tools/sync-preset-descriptions.py` to refresh generated literals.")
        return 1

    print("Preset descriptions are in sync.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
