#!/usr/bin/env python3
"""
Replace the VALUES of existing keys in a Windows .resx catalogue, in place.

For issue #552: the catalogues already have every key, so a translation batch
never adds or removes a key, it only overwrites values. Doing that by hand is
hundreds of Edit calls per locale; doing it with `sed` or a text-mode Python
rewrite silently converts these CRLF files to LF and turns a 300-value change
into a 3,000-line diff.

So this tool:

  * reads and writes bytes, never text mode, so every \\r\\n outside the values
    it touches survives untouched;
  * rewrites only the <value> element of the keys you name, leaving comments,
    attribute order, indentation and the header alone;
  * refuses a key that is not already in the file, so it can never be used to
    add or drop a key and desync the base key count;
  * refuses to change the placeholder set of a value, so a translation cannot
    lose a "{0}" and crash string.Format at runtime;
  * verifies afterwards that the file still parses and still holds exactly the
    same key set it started with.

Input is JSON, {"key": "translated value", ...}, from a file or stdin.

Usage:
    python3 apply_resx_values.py --locale de translations-de.json
    python3 untranslated_resx.py --list de --json > de.json   # authoring round trip
"""
from __future__ import annotations

import argparse
import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[5]
DEFAULT_RESOURCE_DIR = REPO_ROOT / "app/windows/HyperWhisper/Resources"


def escape(value: str) -> str:
    """Escape a value for an XML text node, matching how .resx already stores them.

    app/windows/.gitattributes pins *.resx to `eol=crlf`, and the multi-line
    values in these files use CRLF inside the <value> element too. A JSON "\\n"
    therefore has to become "\\r\\n", or a translated multi-line message would be
    the one place in the file with a bare LF.
    """
    escaped = value.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
    return escaped.replace("\r\n", "\n").replace("\n", "\r\n")


def placeholders(value: str) -> list[str]:
    return sorted(re.findall(r"\{\d+[^}]*\}", value))


def keys_of(text: str) -> list[str]:
    root = ET.fromstring(text)
    return sorted(data.get("name") or "" for data in root.findall("data"))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("translations", help="JSON file of {key: value}, or - for stdin")
    parser.add_argument("--locale", required=True, help="e.g. de, zh-Hans")
    parser.add_argument("--resources", default=str(DEFAULT_RESOURCE_DIR))
    args = parser.parse_args()

    raw = sys.stdin.read() if args.translations == "-" else Path(args.translations).read_text(encoding="utf-8")
    translations: dict[str, str] = json.loads(raw)

    path = Path(args.resources) / f"Strings.{args.locale}.resx"
    if not path.exists():
        print(f"ERROR: no catalogue at {path}")
        return 1

    original = path.read_bytes().decode("utf-8")
    text = original
    keys_before = keys_of(original)

    changed = 0
    for key, value in translations.items():
        if not value:
            continue
        pattern = re.compile(
            r'(<data name="' + re.escape(key) + r'"[^>]*>\s*<value>)(.*?)(</value>)',
            re.DOTALL,
        )
        match = pattern.search(text)
        if match is None:
            print(f"ERROR: key '{key}' is not in {path.name}. This tool only changes values.")
            return 1

        current = match.group(2)
        if placeholders(escape(value)) != placeholders(current):
            print(
                f"ERROR: key '{key}' placeholders would change from "
                f"{placeholders(current)} to {placeholders(escape(value))}."
            )
            return 1

        if current == escape(value):
            continue
        text = text[: match.start(2)] + escape(value) + text[match.end(2) :]
        changed += 1

    if keys_of(text) != keys_before:
        print("ERROR: the key set changed. Refusing to write.")
        return 1

    path.write_bytes(text.encode("utf-8"))
    print(f"{path.name}: {changed} value(s) rewritten, {len(keys_before)} keys unchanged")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
