#!/usr/bin/env python3
"""
Safely insert translations into macOS Localizable.strings files.

This script preserves CRLF line endings and UTF-8 encoding, which are
critical for plutil validation and Xcode builds.

Usage:
    # Single key insertion
    python3 insert_translation.py \
        --file path/to/Localizable.strings \
        --after "existing.key" \
        --key "new.key" \
        --value "translated value"

    # Batch insertion from JSON
    python3 insert_translation.py \
        --file path/to/Localizable.strings \
        --batch translations.json

    # With comment/section header
    python3 insert_translation.py \
        --file path/to/Localizable.strings \
        --after "existing.key" \
        --key "new.key" \
        --value "translated value" \
        --comment "// New Section Header"

JSON batch file format:
{
    "after": "existing.key",
    "translations": [
        {"key": "new.key1", "value": "value1"},
        {"key": "new.key2", "value": "value2", "comment": "// Optional comment"}
    ]
}
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path


def validate_file(path: Path) -> bool:
    """Validate .strings file using plutil."""
    result = subprocess.run(
        ["plutil", "-lint", str(path)],
        capture_output=True,
        text=True
    )
    return result.returncode == 0


def find_line_end(content: bytes, start: int) -> int:
    """Find the end of a line (after CRLF or LF)."""
    crlf = content.find(b'\r\n', start)
    lf = content.find(b'\n', start)

    if crlf != -1 and (lf == -1 or crlf < lf):
        return crlf + 2  # Include CRLF
    elif lf != -1:
        return lf + 1  # Include LF
    return len(content)


def detect_line_ending(content: bytes) -> bytes:
    """Detect the line ending style used in the file."""
    if b'\r\n' in content:
        return b'\r\n'
    return b'\n'


def escape_for_strings(value: str) -> str:
    """Escape special characters for .strings file format."""
    # Escape backslashes first, then quotes
    value = value.replace('\\', '\\\\')
    value = value.replace('"', '\\"')
    return value


def insert_translation(
    file_path: Path,
    after_key: str,
    key: str,
    value: str,
    comment: str | None = None
) -> bool:
    """
    Insert a translation after a specified key.

    Args:
        file_path: Path to the .strings file
        after_key: Key to insert after
        key: New key to insert
        value: Translated value
        comment: Optional comment line to add before the key

    Returns:
        True if successful, False otherwise
    """
    # Read in binary to preserve exact bytes
    with open(file_path, 'rb') as f:
        content = f.read()

    # Detect line ending style
    nl = detect_line_ending(content)

    # Find the marker key
    marker = f'"{after_key}" = '.encode('utf-8')
    idx = content.find(marker)

    if idx == -1:
        print(f"Error: Key '{after_key}' not found in {file_path}", file=sys.stderr)
        return False

    # Find end of that line
    end_line = find_line_end(content, idx)

    # Build the insertion text
    escaped_value = escape_for_strings(value)
    insert_parts = []

    if comment:
        insert_parts.append(comment.encode('utf-8'))

    insert_parts.append(f'"{key}" = "{escaped_value}";'.encode('utf-8'))

    insert_text = nl + nl.join(insert_parts)

    # Insert after the marker line
    new_content = content[:end_line] + insert_text + content[end_line:]

    # Write back in binary
    with open(file_path, 'wb') as f:
        f.write(new_content)

    return True


def batch_insert(file_path: Path, batch_file: Path) -> bool:
    """
    Insert multiple translations from a JSON batch file.

    Args:
        file_path: Path to the .strings file
        batch_file: Path to JSON file with translations

    Returns:
        True if all insertions successful, False otherwise
    """
    with open(batch_file, 'r', encoding='utf-8') as f:
        batch = json.load(f)

    after_key = batch.get('after')
    translations = batch.get('translations', [])

    if not after_key:
        print("Error: 'after' key is required in batch file", file=sys.stderr)
        return False

    if not translations:
        print("Error: 'translations' array is empty", file=sys.stderr)
        return False

    # Insert in reverse order so positions stay valid
    for item in reversed(translations):
        key = item.get('key')
        value = item.get('value')
        comment = item.get('comment')

        if not key or not value:
            print(f"Error: Missing key or value in translation item", file=sys.stderr)
            return False

        if not insert_translation(file_path, after_key, key, value, comment):
            return False

        print(f"  Inserted: {key}")

    return True


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Safely insert translations into macOS Localizable.strings files"
    )
    parser.add_argument(
        "--file", "-f",
        required=True,
        help="Path to the Localizable.strings file"
    )
    parser.add_argument(
        "--after", "-a",
        help="Key to insert after (required for single insertion)"
    )
    parser.add_argument(
        "--key", "-k",
        help="New key to insert"
    )
    parser.add_argument(
        "--value", "-v",
        help="Translated value"
    )
    parser.add_argument(
        "--comment", "-c",
        help="Optional comment line to add before the key"
    )
    parser.add_argument(
        "--batch", "-b",
        help="Path to JSON file for batch insertion"
    )
    parser.add_argument(
        "--validate-only",
        action="store_true",
        help="Only validate the file, don't modify"
    )

    args = parser.parse_args()
    file_path = Path(args.file)

    if not file_path.exists():
        print(f"Error: File not found: {file_path}", file=sys.stderr)
        return 1

    # Validate-only mode
    if args.validate_only:
        if validate_file(file_path):
            print(f"OK: {file_path}")
            return 0
        else:
            print(f"INVALID: {file_path}", file=sys.stderr)
            return 1

    # Validate before modification
    if not validate_file(file_path):
        print(f"Error: File is already invalid: {file_path}", file=sys.stderr)
        print("Restore from git before attempting modifications.", file=sys.stderr)
        return 1

    # Batch mode
    if args.batch:
        batch_path = Path(args.batch)
        if not batch_path.exists():
            print(f"Error: Batch file not found: {batch_path}", file=sys.stderr)
            return 1

        print(f"Batch inserting translations into {file_path}...")
        if not batch_insert(file_path, batch_path):
            return 1

    # Single insertion mode
    elif args.after and args.key and args.value:
        print(f"Inserting '{args.key}' into {file_path}...")
        if not insert_translation(
            file_path,
            args.after,
            args.key,
            args.value,
            args.comment
        ):
            return 1
        print(f"  Inserted: {args.key}")

    else:
        print("Error: Provide either --batch or (--after, --key, --value)", file=sys.stderr)
        parser.print_help()
        return 1

    # Validate after modification
    if validate_file(file_path):
        print(f"Validation: OK")
        return 0
    else:
        print(f"Validation: FAILED - file may be corrupted!", file=sys.stderr)
        print("Consider restoring from git.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
