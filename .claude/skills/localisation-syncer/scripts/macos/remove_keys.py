#!/usr/bin/env python3
"""
Remove a set of localization keys from the macOS Base.lproj/Localizable.strings
and every non-Base locale file.

.strings files are CRLF-sensitive and Xcode will refuse to copy them if line
endings get mangled, so this script works in binary mode — it finds each key's
line, snips it along with its line terminator, and leaves the rest of the file
byte-identical.

Input: a list of keys, either inline (--keys foo.bar baz.qux) or one-per-line
in a file (--keys-file /tmp/to_remove.txt). Blank lines and lines starting
with `#` are ignored.

Usage:
    python3 remove_keys.py --keys-file /tmp/to_remove.txt
    python3 remove_keys.py --keys content.placeholder common.save
    python3 remove_keys.py --keys-file /tmp/to_remove.txt --dry-run

Validates every touched file with `plutil -lint` afterwards. Exits non-zero
if any file fails validation.
"""
from __future__ import annotations

import argparse
import re
import subprocess
import sys
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
    # Preserve order, dedupe
    seen = set()
    unique = []
    for k in keys:
        if k not in seen:
            seen.add(k)
            unique.append(k)
    return unique


def remove_keys_from_file(path: Path, keys: list[str], dry_run: bool) -> int:
    """Return number of keys removed from this file."""
    with path.open("rb") as f:
        content = f.read()

    removed = 0
    for key in keys:
        # Match the whole line starting at a newline (or file start) through
        # its line terminator. The key is stored as "key.name".
        # Regex is applied to bytes to preserve encoding.
        escaped = re.escape(key).encode("utf-8")
        # Line pattern: start-of-line, optional whitespace, "key", optional
        # whitespace, =, anything up to the line terminator (CRLF or LF),
        # consuming the terminator.
        pattern = re.compile(
            rb"(?m)^[ \t]*\"" + escaped + rb"\"[ \t]*=[^\r\n]*(?:\r\n|\n|\r)"
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
    """Return Base.lproj first, then every other *.lproj/Localizable.strings."""
    loc_dir = base.parent.parent  # Base.lproj/ -> Localizations/
    others = sorted(
        p for p in loc_dir.glob("*.lproj/Localizable.strings") if p != base
    )
    return [base] + others


def validate_plutil(paths: list[Path]) -> list[Path]:
    bad: list[Path] = []
    for p in paths:
        try:
            result = subprocess.run(
                ["plutil", "-lint", str(p)],
                capture_output=True,
                text=True,
                check=False,
            )
        except FileNotFoundError:
            print("WARNING: plutil not available; skipping validation")
            return []
        if result.returncode != 0 or "OK" not in result.stdout:
            bad.append(p)
            print(f"FAIL: {p}\n  {result.stdout.strip()}\n  {result.stderr.strip()}")
    return bad


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Remove localization keys from macOS Base + all locale files"
    )
    parser.add_argument(
        "--base",
        default="app/macos/hyperwhisper/Localizations/Base.lproj/Localizable.strings",
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
            print(f"  {p.parent.name}: -{count}")

    print(f"\nRemoved {total_removed} line(s) across {len(touched)} file(s)")

    if args.dry_run or not touched:
        return 0

    print("\nValidating with plutil...")
    bad = validate_plutil(touched)
    if bad:
        print(f"\n{len(bad)} file(s) failed plutil validation — inspect before committing")
        return 2
    print("All touched files pass plutil -lint")
    return 0


if __name__ == "__main__":
    sys.exit(main())
