#!/usr/bin/env python3
"""
Sync shared preset descriptions into platform literals.

Usage:
    python3 tools/sync-preset-descriptions.py
"""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path


PRESET_IDS = ("hyper", "message", "mail", "note", "meeting", "code", "custom")
WINDOWS_PRESET_IDS = ("hyper", "message", "mail", "note", "meeting", "custom", "code")
REQUIRED_FIELDS = ("displayName", "tooltip", "preview", "description")


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


def escape_string(value: str) -> str:
    return (
        value.replace("\\", "\\\\")
        .replace('"', '\\"')
        .replace("\n", "\\n")
        .replace("\r", "\\r")
    )


def title_case_preset_id(preset_id: str) -> str:
    return preset_id[0].upper() + preset_id[1:]


def write_if_changed(path: Path, contents: str) -> bool:
    original = path.read_text(encoding="utf-8")
    if contents == original:
        return False

    path.write_text(contents, encoding="utf-8", newline="")
    return True


def sync_macos_strings(root: Path, presets: dict[str, dict[str, str]]) -> bool:
    path = (
        root
        / "app"
        / "macos"
        / "hyperwhisper"
        / "Localizations"
        / "Base.lproj"
        / "Localizable.strings"
    )
    source = path.read_text(encoding="utf-8")
    replacements: dict[str, str] = {}

    for preset_id in PRESET_IDS:
        preset = presets[preset_id]
        replacements[f"modes.preset.{preset_id}.name"] = preset["displayName"]
        replacements[f"modes.preset.{preset_id}.tooltip"] = preset["tooltip"]
        replacements[f"modes.preset.{preset_id}.preview"] = preset["preview"]

    seen: set[str] = set()
    pattern = re.compile(
        r'^(?P<prefix>"(?P<key>modes\.preset\.[^.]+\.(?:name|tooltip|preview))"\s*=\s*")'
        r'(?P<value>(?:[^"\\]|\\.)*)'
        r'(?P<suffix>";\s*)$'
    )
    lines: list[str] = []

    for line in source.splitlines(keepends=True):
        newline = "\n" if line.endswith("\n") else ""
        body = line[:-1] if newline else line
        match = pattern.match(body)

        if match and match.group("key") in replacements:
            key = match.group("key")
            seen.add(key)
            lines.append(
                f'{match.group("prefix")}{escape_string(replacements[key])}{match.group("suffix")}{newline}'
            )
        else:
            lines.append(line)

    missing = sorted(set(replacements) - seen)
    if missing:
        raise ValueError(
            f"{path.relative_to(root)} is missing keys: {', '.join(missing)}"
        )

    return write_if_changed(path, "".join(lines))


def replace_switch_method(
    source: str,
    method_name: str,
    preset_values: dict[str, str],
    fallback: str,
) -> str:
    newline = "\r\n" if "\r\n" in source else "\n"
    entries = [
        f'        PresetType.{title_case_preset_id(preset_id)} => "{escape_string(preset_values[preset_id])}",'
        for preset_id in WINDOWS_PRESET_IDS
    ]
    entries.append(f"        _ => {fallback}")
    replacement_body = newline.join(entries)

    pattern = re.compile(
        rf"(?P<prefix>public static string {method_name}\(this PresetType preset\) => preset switch\s*"
        r"\{)"
        r"\s*"
        r"(?P<body>.*?)"
        r"(?P<suffix>\s*\};)",
        re.DOTALL,
    )

    def replace(match: re.Match[str]) -> str:
        return (
            f"{match.group('prefix')}{newline}"
            f"{replacement_body}{newline}"
            f"    {match.group('suffix').lstrip()}"
        )

    updated, count = pattern.subn(replace, source, count=1)
    if count != 1:
        raise ValueError(f"Could not find PresetTypeExtensions.{method_name}")

    return updated


def sync_windows_preset_type(root: Path, presets: dict[str, dict[str, str]]) -> bool:
    path = root / "app" / "windows" / "HyperWhisper" / "Models" / "PresetType.cs"
    source = path.read_text(encoding="utf-8")

    display_names = {
        preset_id: presets[preset_id].get(
            "windowsDisplayName",
            presets[preset_id]["displayName"],
        )
        for preset_id in PRESET_IDS
    }
    descriptions = {
        preset_id: presets[preset_id]["description"]
        for preset_id in PRESET_IDS
    }

    updated = replace_switch_method(
        source,
        "ToDisplayName",
        display_names,
        "preset.ToString()",
    )
    updated = replace_switch_method(
        updated,
        "ToDescription",
        descriptions,
        '""',
    )

    return write_if_changed(path, updated)


def main() -> int:
    root = Path(__file__).resolve().parents[1]

    try:
        presets = load_json(root)
        changed = [
            ("macOS Base Localizable.strings", sync_macos_strings(root, presets)),
            ("Windows PresetType.cs", sync_windows_preset_type(root, presets)),
        ]
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(f"ERROR: {error}")
        return 1

    for label, was_changed in changed:
        status = "updated" if was_changed else "already in sync"
        print(f"{label}: {status}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
