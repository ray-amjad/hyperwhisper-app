#!/usr/bin/env python3
"""
Validate the Drizzle migration journal before a pull request merges.

Drizzle's Postgres migrator tracks progress with a single watermark. It runs a
migration only when `lastDbMigration.created_at < migration.folderMillis`, where
folderMillis is the journal entry's `when`. It never checks file hashes or
names. Two consequences drive every rule below:

  * an entry whose `when` is not greater than the one before it is silently
    skipped on any database that has already crossed that watermark;
  * editing the SQL of an already-applied migration changes nothing in
    production. The database has passed that watermark, so the new content
    never runs, while a fresh database applies it and drifts from production.

The build applies pending migrations on every deploy, so both failures land in
production the moment the pull request merges. This script is the gate.

The migrations directory is found automatically, so this file is identical in
every repository. Override it when a repository has more than one.

Usage:
    python3 .github/scripts/validate_drizzle_journal.py
    python3 .github/scripts/validate_drizzle_journal.py --base origin/main
    python3 .github/scripts/validate_drizzle_journal.py --dir apps/web/drizzle
"""

import argparse
import json
import os
import re
import subprocess
import sys

# drizzle-kit names every migration it generates `NNNN_words.sql`. A .sql file
# in the migrations directory that does not have that shape is a hand-run
# helper (custom triggers, seeds) that the migrator was never meant to see, so
# the rules below leave it alone.
GENERATED_SQL = re.compile(r"^\d{4}_")

# A repository whose history already contains an ordering violation cannot fix
# it: the entry has been applied and recorded, and re-stamping it would only
# hide the entry from databases that already ran it. Without a way to record
# such a case, the check stays red forever and can never be a required check,
# which is the same as not having it. This file records those, one object per
# entry, and each one must be reconciled against the live database first.
BASELINE_PATH = ".github/drizzle-journal-baseline.json"

# Wording reused by every history failure. All of them have the same fix.
APPEND_ONLY_FIX = (
    "Migrations are append-only once they reach main. To undo a migration, add "
    "a brand-new one (next number, fresh `when` from `date +%s000`) that "
    "explicitly reverses it, for example `DROP INDEX IF EXISTS`. Reusing an old "
    "slot's `when` does not work: the replacement never runs on a database that "
    "already crossed that watermark, which is production."
)


def run(*args):
    """Run a git command and return stdout, or None when it fails."""
    result = subprocess.run(
        args, capture_output=True, text=True, cwd=os.getcwd()
    )
    if result.returncode != 0:
        return None
    return result.stdout


def journal_path(drizzle_dir):
    return f"{drizzle_dir}/meta/_journal.json"


def discover_drizzle_dir():
    """Find the migrations directory, so this file needs no per-repo edit.

    Only tracked files count. That keeps build output, vendored copies and
    node_modules from being mistaken for the real migrations directory.
    Returns (dir, error).
    """
    listed = run("git", "ls-files", "*drizzle/meta/_journal.json")
    if listed is None:
        return None, "Not a git repository, so the journal cannot be found."

    found = sorted(line.strip() for line in listed.splitlines() if line.strip())
    if not found:
        return None, None
    if len(found) > 1:
        dirs = [path[: -len("/meta/_journal.json")] for path in found]
        return None, (
            "More than one Drizzle journal is tracked in this repository: "
            f"{dirs}. Pass the right one with --dir, or set DRIZZLE_DIR in "
            "the workflow."
        )
    return found[0][: -len("/meta/_journal.json")], None


def load_baseline():
    """Read the recorded pre-existing violations. Returns (allowed, errors).

    `allowed` maps (idx, tag, when) to the reason it was accepted. Keying on
    all three means a later edit to the entry stops matching and fails again,
    so this can wave through history without weakening the rule.
    """
    if not os.path.exists(BASELINE_PATH):
        return {}, []

    try:
        with open(BASELINE_PATH, "r") as handle:
            data = json.load(handle)
    except json.JSONDecodeError as exc:
        return {}, [f"{BASELINE_PATH} is not valid JSON: {exc}"]

    listed = data.get("allow_out_of_order", [])
    if not isinstance(listed, list):
        return {}, [f"{BASELINE_PATH} needs `allow_out_of_order` to be a list."]

    allowed, errors = {}, []
    for item in listed:
        missing = [k for k in ("idx", "tag", "when", "why") if not item.get(k)]
        if missing:
            errors.append(
                f"{BASELINE_PATH} has an entry missing {missing}: {item}. Every "
                "accepted violation needs `idx`, `tag`, `when` and a `why` "
                "saying how it was reconciled against the live database."
            )
            continue
        allowed[(item["idx"], item["tag"], item["when"])] = item["why"]
    return allowed, errors


def load_journal(raw, where):
    """Parse journal JSON and return (entries, errors)."""
    try:
        journal = json.loads(raw)
    except json.JSONDecodeError as exc:
        return None, [f"{where} is not valid JSON: {exc}"]

    entries = journal.get("entries")
    if not isinstance(entries, list):
        return None, [f"{where} has no `entries` array."]

    for entry in entries:
        for key in ("idx", "when", "tag"):
            if key not in entry:
                return None, [f"{where} has an entry missing `{key}`: {entry}"]
    return entries, []


def check_structure(entries, allowed):
    """Rules that hold for the journal on its own, with no history.

    Returns (errors, notes). A violation listed in the baseline file becomes a
    note, so it stays visible without failing the check for ever.
    """
    errors = []
    notes = []

    # The watermark only ever moves forward, so each entry must beat the
    # highest `when` before it, not merely the one directly above it. One
    # entry stamped far in the future hides every entry after it, and a
    # neighbour-only comparison would report just the first of them.
    for i in range(1, len(entries)):
        curr = entries[i]
        highest = max(entries[:i], key=lambda entry: entry["when"])
        if curr["when"] <= highest["when"]:
            key = (curr["idx"], curr["tag"], curr["when"])
            if key in allowed:
                notes.append(
                    f"idx={curr['idx']} ({curr['tag']}) is out of order and is "
                    f"accepted by {BASELINE_PATH}: {allowed[key]}"
                )
                continue
            errors.append(
                f"Entry idx={curr['idx']} ({curr['tag']}, when={curr['when']}) "
                f"does not come after idx={highest['idx']} ({highest['tag']}, "
                f"when={highest['when']}), the newest entry above it. Drizzle "
                f"would skip it on any database that already ran "
                f"idx={highest['idx']}. Give it a `when` that is strictly "
                f"greater, from `date +%s000`."
            )

    expected = list(range(len(entries)))
    actual = [entry["idx"] for entry in entries]
    if actual != expected:
        errors.append(
            f"`idx` values must run 0..{len(entries) - 1} in order. Got: {actual}"
        )

    tags = [entry["tag"] for entry in entries]
    duplicates = sorted({tag for tag in tags if tags.count(tag) > 1})
    if duplicates:
        errors.append(f"Duplicate `tag` values in the journal: {duplicates}")

    return errors, notes


def check_files_match(entries, drizzle_dir):
    """Every entry needs its SQL file, and every SQL file needs its entry."""
    if not os.path.isdir(drizzle_dir):
        return [f"{drizzle_dir} does not exist."]

    on_disk = {
        name[:-4] for name in os.listdir(drizzle_dir) if name.endswith(".sql")
    }
    tagged = {entry["tag"] for entry in entries}

    errors = []
    missing = sorted(tagged - on_disk)
    if missing:
        errors.append(
            "Journal entries with no SQL file (drizzle would fail at "
            f"deploy time): {missing}"
        )
    orphans = sorted(
        stem for stem in on_disk - tagged if GENERATED_SQL.match(stem)
    )
    if orphans:
        errors.append(
            f"{orphans} sit in {drizzle_dir} named like generated migrations, "
            "but no journal entry lists them, so the migrator never runs them. "
            "Anyone reading the directory will assume they are applied. Either "
            "add them to the journal, or rename them without the four-digit "
            "prefix so they read as hand-run SQL."
        )
    return errors


def check_history(entries, drizzle_dir, base_ref):
    """Compare against the base branch. Migrations are append-only."""
    path = journal_path(drizzle_dir)

    merge_base = run("git", "merge-base", base_ref, "HEAD")
    if merge_base is None:
        print(
            f"note: cannot resolve `{base_ref}`, skipping the history checks.",
            file=sys.stderr,
        )
        return []
    merge_base = merge_base.strip()

    base_raw = run("git", "show", f"{merge_base}:{path}")
    if base_raw is None:
        print(
            "note: the base commit has no journal, skipping the history checks.",
            file=sys.stderr,
        )
        return []

    base_entries, errors = load_journal(base_raw, f"{base_ref}:{path}")
    if errors:
        # A broken journal on the base branch is not this pull request's fault.
        print(f"note: {errors[0]} Skipping the history checks.", file=sys.stderr)
        return []

    base_by_idx = {entry["idx"]: entry for entry in base_entries}
    head_by_idx = {entry["idx"]: entry for entry in entries}

    for idx, base_entry in base_by_idx.items():
        head_entry = head_by_idx.get(idx)
        if head_entry is None:
            errors.append(
                f"Journal entry idx={idx} ({base_entry['tag']}) was removed. "
                f"Production has already applied it. {APPEND_ONLY_FIX}"
            )
            continue
        for key in ("when", "tag"):
            if head_entry[key] != base_entry[key]:
                errors.append(
                    f"Journal entry idx={idx} changed `{key}` from "
                    f"{base_entry[key]!r} to {head_entry[key]!r}. "
                    f"{APPEND_ONLY_FIX}"
                )

    highest_base_when = max((e["when"] for e in base_entries), default=0)
    for entry in entries:
        if entry["idx"] not in base_by_idx and entry["when"] <= highest_base_when:
            errors.append(
                f"New entry idx={entry['idx']} ({entry['tag']}) has "
                f"when={entry['when']}, which is not past the newest entry "
                f"already on {base_ref} ({highest_base_when}). Drizzle would "
                f"never run it on production. Stamp it with `date +%s000`."
            )

    changed = run(
        "git", "diff", "--name-status", f"{merge_base}...HEAD", "--", drizzle_dir
    )
    # Only files the base branch already applied are dangerous to touch. A
    # hand-run helper next to them is not a migration, so editing it is fine.
    applied = {entry["tag"] for entry in base_entries}
    if changed:
        for line in changed.splitlines():
            parts = line.split("\t")
            status, path = parts[0], parts[-1]
            # A rename reports the old path first and the new one last. The
            # old name is the one the base branch applied.
            was = parts[1] if status.startswith("R") else path
            if not was.endswith(".sql"):
                continue
            if os.path.basename(was)[:-4] not in applied:
                continue
            if status.startswith("M"):
                errors.append(
                    f"{path} was edited. Its migration has already run on "
                    f"production, so the new SQL will never be applied there. "
                    f"{APPEND_ONLY_FIX}"
                )
            elif status.startswith("D"):
                errors.append(f"{path} was deleted. {APPEND_ONLY_FIX}")
            elif status.startswith("R"):
                errors.append(f"{path} was renamed. {APPEND_ONLY_FIX}")

    return errors


def report(errors):
    print("Drizzle journal check failed.\n")
    for error in errors:
        print(f"  - {error}\n")
    for doc in ("CLAUDE.md", ".claude/CLAUDE.md", "AGENTS.md"):
        if os.path.exists(doc):
            print(f"Background: {doc}, the section on editing a migration.")
            break
    return 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--base",
        default=os.environ.get("BASE_REF", ""),
        help="Branch to treat as already deployed, for example origin/main. "
        "Without it only the structural checks run.",
    )
    parser.add_argument(
        "--dir",
        default=os.environ.get("DRIZZLE_DIR", ""),
        help="Migrations directory. Found automatically when there is only "
        "one in the repository.",
    )
    args = parser.parse_args()

    drizzle_dir = args.dir.rstrip("/")
    if not drizzle_dir:
        drizzle_dir, error = discover_drizzle_dir()
        if error:
            return report([error])
        if not drizzle_dir:
            print("No Drizzle journal in this repository, nothing to check.")
            return 0

    path = journal_path(drizzle_dir)
    if not os.path.exists(path):
        print(f"No {path}, nothing to check.")
        return 0

    with open(path, "r") as handle:
        entries, errors = load_journal(handle.read(), path)

    allowed, baseline_errors = load_baseline()
    errors += baseline_errors

    notes = []
    if entries is not None:
        structural, notes = check_structure(entries, allowed)
        errors += structural
        errors += check_files_match(entries, drizzle_dir)
        if args.base:
            errors += check_history(entries, drizzle_dir, args.base)

    for note in notes:
        print(f"note: {note}")

    if errors:
        return report(errors)

    count = len(entries) if entries else 0
    print(
        f"Drizzle journal is fine: {count} migrations in {drizzle_dir}, "
        "correctly ordered."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
