#!/usr/bin/env python3
"""Generate Linux satellite catalogs only from exact macOS English-value matches.

Linux-specific copy deliberately stays in the invariant catalog until a human-reviewed
translation is supplied. This generator must never call a translation service.
"""

from pathlib import Path
import re
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
LINUX = Path(__file__).resolve().parent / "Resources"
MAC = ROOT / "macos" / "hyperwhisper" / "Localizations"
ENTRY = re.compile(r'^"(?P<key>[^"\\]+)"\s*=\s*"(?P<value>(?:[^"\\]|\\.)*)";', re.MULTILINE)


def strings(path: Path) -> dict[str, str]:
    values: dict[str, str] = {}
    for match in ENTRY.finditer(path.read_text(encoding="utf-8")):
        value = match.group("value").replace(r'\"', '"').replace(r"\n", "\n").replace(r"\\", "\\")
        values[match.group("key")] = value
    return values


def linux_values() -> dict[str, str]:
    root = ET.parse(LINUX / "LinuxStrings.resx").getroot()
    return {item.attrib["name"]: item.findtext("value", "") for item in root.findall("data")}


base = strings(MAC / "Base.lproj" / "Localizable.strings")
by_english = {value: key for key, value in base.items()}
reusable = {key: by_english[value] for key, value in linux_values().items() if value in by_english}
if len(reusable) < 7:
    raise SystemExit("Expected at least seven exact semantic matches with the macOS catalog.")

for directory in sorted(MAC.glob("*.lproj")):
    culture = directory.name.removesuffix(".lproj")
    if culture == "Base":
        continue
    localized = strings(directory / "Localizable.strings")
    root = ET.Element("root")
    for linux_key, mac_key in sorted(reusable.items()):
        if mac_key not in localized:
            raise SystemExit(f"{culture} is missing macOS key {mac_key}")
        data = ET.SubElement(root, "data", {"name": linux_key, "xml:space": "preserve"})
        ET.SubElement(data, "value").text = localized[mac_key]
    ET.indent(root, space="  ")
    output = LINUX / f"LinuxStrings.{culture}.resx"
    ET.ElementTree(root).write(output, encoding="utf-8", xml_declaration=True)

print(f"Generated {len(list(MAC.glob('*.lproj'))) - 1} satellites with {len(reusable)} exact reusable keys each.")
