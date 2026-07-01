#!/usr/bin/env python3
"""
Remove a set of localization keys from the Windows base Strings.resx and
every Strings.<locale>.resx file.

Each key is stored as a multi-line `<data name="KEY" xml:space="preserve">
  <value>…</value>
</data>` block. This script snips the whole block (including the trailing
newline) in-place, leaving the rest of the file byte-identical. The parser
is intentionally string-based rather than DOM-based so indentation, comments,
the XSD header, and blank lines between blocks are preserved exactly.

Input: a list of keys, either inline (--keys foo.bar baz.qux) or one-per-line
in a file (--keys-file /tmp/to_remove.txt). Blank lines and lines starting
with `#` are ignored.

Usage:
    python3 remove_keys.py --keys-file /tmp/to_remove.txt
    python3 remove_keys.py --keys common.remove common.close
    python3 remove_keys.py --keys-file /tmp/to_remove.txt --dry-run

Validates every touched file with `xml.etree.ElementTree.parse` afterwards.
Exits non-zero if any file fails to parse.
"""
from __future__ import annotations

import argparse
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def read_keys(keys_file: Path | None, keys_inline: list[str] | None) -> list[str]:
    keys: list[str] = []
    if keys_file is not None:
        for raw in keys_file.read_text(encoding="utf-8").splitlines():
            line = raw.strip()
            if not line or line.startswith("#"):
                continue
            keys.append(line)
    if keys_inline:
        keys.extend(keys_inline)
    seen = set()
    unique = []
    for k in keys:
        if k not in seen:
            seen.add(k)
            unique.append(k)
    return unique


def remove_keys_from_file(path: Path, keys: list[str], dry_run: bool) -> int:
    # Read and write in binary mode so CRLF line endings are preserved byte
    # for byte — Python text mode applies universal-newlines translation
    # (CRLF -> LF on read, LF-only on write), which would rewrite every line
    # in the file and cause a massive spurious diff.
    with path.open("rb") as f:
        content = f.read()

    removed = 0
    for key in keys:
        escaped = re.escape(key).encode("utf-8")
        # Pattern: optional indent, <data name="KEY" ...>...</data>, trailing
        # line terminator (CRLF, LF, or bare CR). The `.*?` under DOTALL may
        # match across lines, but the non-greedy quantifier + the explicit
        # </data> closer prevents it from devouring multiple blocks.
        pattern = re.compile(
            rb"[ \t]*<data\s+name=\"" + escaped + rb"\"[^>]*>.*?</data>[ \t]*(?:\r\n|\n|\r)",
            re.DOTALL,
        )
        new_content, count = pattern.subn(b"", content)
        if count:
            removed += count
            content = new_content

    if removed and not dry_run:
        with path.open("wb") as f:
            f.write(content)
    return removed


def discover_locale_files(base: Path) -> list[Path]:
    """Return base Strings.resx first, then every Strings.*.resx."""
    res_dir = base.parent
    others = sorted(p for p in res_dir.glob("Strings.*.resx") if p != base)
    return [base] + others


def validate_xml(paths: list[Path]) -> list[Path]:
    bad: list[Path] = []
    for p in paths:
        try:
            ET.parse(p)
        except ET.ParseError as e:
            bad.append(p)
            print(f"FAIL: {p}\n  {e}")
    return bad


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Remove localization keys from Windows Strings.resx + all locales"
    )
    parser.add_argument(
        "--base",
        default="app/windows/HyperWhisper/Resources/Strings.resx",
    )
    parser.add_argument("--keys-file", type=Path, default=None)
    parser.add_argument("--keys", nargs="*", default=None)
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Report what would be removed without writing any files",
    )
    args = parser.parse_args()

    base_path = Path(args.base)
    if not base_path.exists():
        print(f"ERROR: Base file not found: {base_path}")
        return 1

    keys = read_keys(args.keys_file, args.keys)
    if not keys:
        print("ERROR: no keys specified. Use --keys-file or --keys.")
        return 1

    paths = discover_locale_files(base_path)
    print(f"Removing {len(keys)} key(s) from {len(paths)} file(s)")
    if args.dry_run:
        print("(dry run — no files will be written)")

    total_removed = 0
    touched: list[Path] = []
    for p in paths:
        count = remove_keys_from_file(p, keys, dry_run=args.dry_run)
        if count:
            touched.append(p)
            total_removed += count
            label = p.stem.replace("Strings.", "")
            if label == "Strings":
                label = "base"
            print(f"  {label}: -{count}")

    print(f"\nRemoved {total_removed} block(s) across {len(touched)} file(s)")

    if args.dry_run or not touched:
        return 0

    print("\nValidating XML...")
    bad = validate_xml(touched)
    if bad:
        print(f"\n{len(bad)} file(s) failed XML parsing — inspect before committing")
        return 2
    print("All touched files parse as valid XML")
    return 0


if __name__ == "__main__":
    sys.exit(main())
