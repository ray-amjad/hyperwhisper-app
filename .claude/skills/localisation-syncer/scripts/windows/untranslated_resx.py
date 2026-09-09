#!/usr/bin/env python3
"""
Measure how much of each Windows .resx catalogue is still raw English.

`compare_resx.py` next to this file answers "does every catalogue have every
KEY". This one answers the different question issue #552 is about: "does every
catalogue have every VALUE in its own language". A locale file can carry all 864
keys, load cleanly, pass every existing gate, and still be half English.

A value that is byte-identical to the English base value is one of two things:

  * Identical BY DESIGN - a product name ("HyperWhisper"), a vendor
    ("Deepgram"), a model id ("Nova 3 General"), or a value with no letters in
    it at all ("{0}", "127.0.0.1"). Translating these would be a regression.
  * Genuinely UNTRANSLATED - "Delete Selected", "Recording cancelled",
    "Failed to load transcription model".

The by-design set is recorded, key by key with a reason, in
`app/windows/HyperWhisper/Resources/translation-status.json`. Everything else
that is identical counts as untranslated, and that count is what the per-locale
ceiling in the same file bounds.

`HyperWhisper.Localization.CatalogValidator` enforces the ceilings on every
build, so this script and CI read the same file and agree by construction. This
script is the authoring tool: it reports, it reseeds the ceilings after a
translation batch, and it lists the outstanding keys for one locale.

Usage:
    python3 untranslated_resx.py                  # per-locale report
    python3 untranslated_resx.py --markdown       # report as a Markdown table
    python3 untranslated_resx.py --list de        # keys still English in de
    python3 untranslated_resx.py --list de --json # same, as JSON, for tooling
    python3 untranslated_resx.py --seed           # rewrite ceilings to today's counts

Exit status is 2 if any locale is over its recorded ceiling, 0 otherwise.
"""
from __future__ import annotations

import argparse
import json
import xml.etree.ElementTree as ET
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[5]
DEFAULT_RESOURCE_DIR = REPO_ROOT / "app/windows/HyperWhisper/Resources"
STATUS_FILENAME = "translation-status.json"


def parse_resx(path: Path) -> dict[str, str]:
    """Map every resource key in a .resx to its string value."""
    values: dict[str, str] = {}
    root = ET.parse(path).getroot()
    for data in root.findall("data"):
        name = data.get("name")
        if not name:
            continue
        value = data.find("value")
        values[name] = (value.text or "") if value is not None else ""
    return values


def has_letters(value: str) -> bool:
    """True if the value contains anything a translator could actually change.

    A value like "{0}" or "127.0.0.1:{0}" is the same in every language, so it
    is exempt without needing a line in translation-status.json.
    """
    return any(character.isalpha() for character in value)


def load_status(resource_dir: Path) -> dict:
    return json.loads((resource_dir / STATUS_FILENAME).read_text(encoding="utf-8"))


def locale_of(path: Path) -> str:
    return path.name[len("Strings.") : -len(".resx")]


def measure(resource_dir: Path) -> tuple[dict[str, str], dict[str, list[str]], dict]:
    """Return (base catalogue, untranslated keys per locale, status file)."""
    status = load_status(resource_dir)
    by_design = set(status["identicalByDesign"])
    base = parse_resx(resource_dir / "Strings.resx")

    untranslated: dict[str, list[str]] = {}
    for path in sorted(resource_dir.glob("Strings.*.resx")):
        catalog = parse_resx(path)
        untranslated[locale_of(path)] = sorted(
            key
            for key, english in base.items()
            if key not in by_design
            and has_letters(english)
            and catalog.get(key) == english
        )
    return base, untranslated, status


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--resources", default=str(DEFAULT_RESOURCE_DIR))
    parser.add_argument("--list", metavar="LOCALE", help="print the outstanding keys for one locale")
    parser.add_argument("--json", action="store_true", help="with --list, emit JSON")
    parser.add_argument("--markdown", action="store_true", help="emit the report as a Markdown table")
    parser.add_argument("--seed", action="store_true", help="rewrite the ceilings to today's counts")
    args = parser.parse_args()

    resource_dir = Path(args.resources)
    base, untranslated, status = measure(resource_dir)
    ceilings: dict[str, int] = status["ceilings"]
    translatable = sum(1 for key, value in base.items() if key not in set(status["identicalByDesign"]) and has_letters(value))

    if args.list:
        if args.list not in untranslated:
            print(f"ERROR: unknown locale '{args.list}'")
            return 1
        keys = untranslated[args.list]
        if args.json:
            print(json.dumps({key: base[key] for key in keys}, ensure_ascii=False, indent=2))
        else:
            for key in keys:
                print(f"{key}\t{base[key]}")
        return 0

    if args.seed:
        status["ceilings"] = {locale: len(keys) for locale, keys in sorted(untranslated.items())}
        path = resource_dir / STATUS_FILENAME
        path.write_text(json.dumps(status, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print(f"Reseeded {len(status['ceilings'])} ceilings in {path}")
        return 0

    print(f"Base catalogue: {len(base)} keys, {len(status['identicalByDesign'])} identical by design, "
          f"{translatable} translatable")

    over = 0
    if args.markdown:
        print("\n| Locale | Untranslated | Share | Ceiling |")
        print("|---|---:|---:|---:|")
    for locale, keys in sorted(untranslated.items(), key=lambda item: (-len(item[1]), item[0])):
        ceiling = ceilings.get(locale)
        share = f"{len(keys) * 100 / translatable:.0f}%"
        if ceiling is not None and len(keys) > ceiling:
            over += 1
        if args.markdown:
            print(f"| {locale} | {len(keys)} | {share} | {ceiling if ceiling is not None else '-'} |")
        else:
            flag = ""
            if ceiling is None:
                flag = "  NO CEILING"
            elif len(keys) > ceiling:
                flag = f"  OVER CEILING {ceiling}"
            elif len(keys) < ceiling:
                flag = f"  ceiling {ceiling} can drop to {len(keys)}"
            print(f"{locale:>8} {len(keys):4} / {translatable} {share:>4}{flag}")

    total = sum(len(keys) for keys in untranslated.values())
    print(f"\nTotal untranslated values across {len(untranslated)} locales: {total}")
    return 2 if over else 0


if __name__ == "__main__":
    raise SystemExit(main())
