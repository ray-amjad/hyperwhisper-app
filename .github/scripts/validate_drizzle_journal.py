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
    Returns (dir, error, ambiguous).

    `ambiguous` is set only when more than one journal is tracked and
    auto-discovery genuinely cannot pick one. This is a security-relevant
    gate -- it exists to catch tampering with already-applied migrations --
    so ambiguity fails loud rather than silently skipping every check on
    whatever journal(s) actually exist. A quieter option was considered
    (only treat it as ambiguous when more than one candidate looks like a
    *real* migrations directory), but that heuristic is itself gameable: an
    attacker who wants this check to skip only needs to drop one more
    trivial-looking `*drizzle/meta/_journal.json` file anywhere in the repo,
    which is exactly what discovery is supposed to notice, not launder past.
    Set DRIZZLE_DIR in the workflow (or --dir) to bypass discovery entirely
    and pick one explicitly; that path never calls this function.
    """
    listed = run("git", "ls-files", "*drizzle/meta/_journal.json")
    if listed is None:
        return None, "Not a git repository, so the journal cannot be found.", False

    found = sorted(line.strip() for line in listed.splitlines() if line.strip())
    if not found:
        return None, None, False
    if len(found) > 1:
        dirs = [path[: -len("/meta/_journal.json")] for path in found]
        return (
            None,
            (
                "More than one Drizzle journal is tracked in this repository: "
                f"{dirs}. Auto-discovery cannot tell which is the real "
                "migrations directory, and this is a security-relevant check, "
                "so it fails rather than guessing or skipping. Set "
                "DRIZZLE_DIR in the workflow to pick one explicitly."
            ),
            True,
        )
    return found[0][: -len("/meta/_journal.json")], None, False


def discover_drizzle_dir_at(ref):
    """Find the migrations directory as it existed at `ref`, not HEAD.

    Best effort, for the caller that needs to know where the journal lived
    on the base branch -- either because the current tree has none at all
    (deleted wholesale, so `discover_drizzle_dir()` finds nothing to key
    off) or to check whether that directory specifically has since
    disappeared, even when some other, unrelated journal exists on HEAD.

    `ref` must be the exact same commit the caller then loads the journal's
    *content* from (normally the merge-base, via `resolve_merge_base()`).
    Resolving the directory at one ref (say, the base branch's moving tip)
    and its content at another (the merge-base) lets a rename on the base
    branch after the fork point desync the two: the directory resolves at
    its new, post-rename path, `git show <merge-base>:<new-path>` finds
    nothing there because the rename hadn't happened yet at the merge-base,
    and the caller reads that as "no journal on the base branch" instead of
    resolving it correctly.

    Any resolution failure or ambiguity quietly returns None; the caller
    treats that the same as "nothing to compare against", which is what the
    unmodified discovery already does for those cases.
    """
    listed = run("git", "ls-tree", "-r", "--name-only", ref)
    if listed is None:
        return None
    found = sorted(
        line.strip()
        for line in listed.splitlines()
        if line.strip().endswith("drizzle/meta/_journal.json")
    )
    if len(found) != 1:
        return None
    return found[0][: -len("/meta/_journal.json")]


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


def check_structure(entries, allowed, base_keys):
    """Rules that hold for the journal on its own, with no history.

    Returns (errors, notes). A violation listed in the baseline file becomes a
    note, so it stays visible without failing the check for ever -- but only
    when the entry it excuses (its idx/tag/when) already existed on the base
    branch. `base_keys` is the set of (idx, tag, when) tuples already on the
    base branch (empty when there is nothing to compare against). Without
    that restriction a pull request could add a genuinely out-of-order new
    migration and a baseline entry excusing it in the same commit, which
    would defeat the whole check.
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
            if key in allowed and key in base_keys:
                notes.append(
                    f"idx={curr['idx']} ({curr['tag']}) is out of order and is "
                    f"accepted by {BASELINE_PATH}: {allowed[key]}"
                )
                continue
            message = (
                f"Entry idx={curr['idx']} ({curr['tag']}, when={curr['when']}) "
                f"does not come after idx={highest['idx']} ({highest['tag']}, "
                f"when={highest['when']}), the newest entry above it. Drizzle "
                f"would skip it on any database that already ran "
                f"idx={highest['idx']}. Give it a `when` that is strictly "
                f"greater, from `date +%s000`."
            )
            if key in allowed:
                message += (
                    f" {BASELINE_PATH} excuses this idx/tag/when, but it is "
                    "not yet on the base branch -- a baseline entry only "
                    "counts once it, and the violation it excuses, have "
                    "already merged."
                )
            errors.append(message)

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

    # A dangling symlink or a directory named like a migration would still
    # show up in os.listdir(); require a real regular file so drizzle-kit
    # (which reads file contents, not directory entries) can't be fooled the
    # other way -- treat those the same as "not on disk" so the existing
    # "expected but not on disk" error path below catches them.
    sql_names = [
        name
        for name in os.listdir(drizzle_dir)
        if name.endswith(".sql")
        and not os.path.islink(os.path.join(drizzle_dir, name))
        and os.path.isfile(os.path.join(drizzle_dir, name))
    ]
    on_disk = {name[:-4] for name in sql_names}
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

    # drizzle-kit orders migrations by this four-digit prefix alone -- not by
    # the journal's `idx` or `tag` -- so two files sharing one collide even
    # when the journal itself has no duplicate idx/tag (for example, a merge
    # conflict resolved by hand can give the two entries different idx
    # values while both files keep the same prefix). #1041 and #1045 both
    # independently claimed migration index 0096. This used to be a
    # standalone `ls | cut -d_ -f1 | uniq -d` step in ci.yml; it moved here
    # so the coverage survives that step being removed.
    by_prefix = {}
    for name in sql_names:
        if not GENERATED_SQL.match(name):
            continue
        by_prefix.setdefault(name[:4], []).append(name)
    for prefix, colliding in sorted(by_prefix.items()):
        if len(colliding) > 1:
            errors.append(
                f"More than one .sql file in {drizzle_dir} starts with the "
                f"numeric prefix {prefix}: {sorted(colliding)}. drizzle-kit "
                "sorts migrations by this prefix alone, so two files sharing "
                "one collide even though their journal `idx`/`tag` differ. "
                "Renumber one of them to a free four-digit prefix."
            )

    return errors


def resolve_merge_base(base_ref):
    """Resolve the merge-base (fork point) of `base_ref` and HEAD.

    Called once from `main()`, up front, so every base-branch question this
    file asks -- which directory holds the journal there, and what it
    contained -- is anchored to the exact same commit. `check_history()`
    already diffs `merge_base...HEAD`, so this keeps that consistent too.
    Returns None, with a `note:` printed to stderr, when `base_ref` doesn't
    resolve.
    """
    merge_base = run("git", "merge-base", base_ref, "HEAD")
    if merge_base is None:
        print(
            f"note: cannot resolve `{base_ref}`, skipping the history checks.",
            file=sys.stderr,
        )
        return None
    return merge_base.strip()


def load_base_entries(path, merge_base, base_ref):
    """Load the journal at `path` as of `merge_base`. Returns entries or None.

    `merge_base` must already be resolved (see `resolve_merge_base()`) and
    must be the same commit used to resolve `path`'s directory via
    `discover_drizzle_dir_at()` -- see the warning on that function about why
    the two refs have to match. `base_ref` is only used for messages.

    Returns None when the base branch has no journal at that path, or its
    journal doesn't parse; a `note:` explaining why is printed to stderr in
    each case, same as the other git-failure sites in this file.
    """
    base_raw = run("git", "show", f"{merge_base}:{path}")
    if base_raw is None:
        print(
            "note: the base commit has no journal, skipping the history checks.",
            file=sys.stderr,
        )
        return None

    base_entries, errors = load_journal(base_raw, f"{base_ref}:{path}")
    if errors:
        # A broken journal on the base branch is not this pull request's fault.
        print(f"note: {errors[0]} Skipping the history checks.", file=sys.stderr)
        return None

    return base_entries


def check_history(entries, drizzle_dir, base_ref, merge_base, base_entries):
    """Compare against the base branch. Migrations are append-only.

    `merge_base` and `base_entries` come from `load_base_entries()`, loaded
    once in `main()` so the same base journal backs both this and the
    baseline-bypass restriction in `check_structure()`. `base_entries` is
    None when there is nothing to compare against; `main()` has already
    printed why, so this just returns no errors.

    `entries` may be None: the current tree has no journal at all (it was
    deleted). `head_by_idx` then comes out empty, so every base entry below
    reports as removed -- exactly the outcome we want when a pull request
    deletes the journal wholesale instead of editing it.
    """
    if merge_base is None or base_entries is None:
        return []

    errors = []
    base_by_idx = {entry["idx"]: entry for entry in base_entries}
    head_by_idx = {entry["idx"]: entry for entry in (entries or [])}

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
    for entry in entries or []:
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
    if changed is None:
        print(
            f"note: `git diff --name-status {merge_base}...HEAD -- "
            f"{drizzle_dir}` failed, skipping the file-status checks.",
            file=sys.stderr,
        )
    elif changed:
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
            if status.startswith("A"):
                # A brand-new file can't collide with an already-applied tag
                # through drizzle-kit, but nothing stops a hand-crafted
                # commit from trying; the checks above already treat that
                # combination as impossible to reach, so there is nothing
                # more to flag here.
                continue
            if status.startswith("D"):
                errors.append(f"{path} was deleted. {APPEND_ONLY_FIX}")
            elif status.startswith("R"):
                errors.append(f"{path} was renamed. {APPEND_ONLY_FIX}")
            else:
                # Covers "M" (edited) and anything else git reports for an
                # already-applied file -- including "T" (typechange, e.g. a
                # symlink swapped in over the applied .sql) and "C" (copy).
                # Only a clean, unrelated "A" above is safe to ignore.
                errors.append(
                    f"{path} was edited. Its migration has already run on "
                    f"production, so the new SQL will never be applied there. "
                    f"{APPEND_ONLY_FIX}"
                )

    return errors


def check_base_dir_not_deleted(base_dir, base_entries, base_ref, drizzle_dir, entries):
    """The base branch's own journal directory must still have a journal.

    `discover_drizzle_dir()` only ever looks at whichever directories exist
    right now, so a pull request that deletes the real journal directory
    while adding one new, unrelated journal-shaped file elsewhere makes
    discovery find exactly one match on HEAD -- the new file -- and
    `drizzle_dir` becomes that. The "discovery found nothing, fall back to
    where the journal lived on the base branch" path in `main()` never
    fires, because discovery did not find nothing. This checks the base
    branch's actual directory directly, independent of whatever
    `drizzle_dir` ends up being validated structurally, so that shell game
    can't hide a deletion.

    Skipped when the base directory IS the one being validated:
    `check_history()` already reports that case entry by entry, and
    duplicating the message here would just be noise.

    A plain directory rename (`git mv drizzle db/drizzle`, nothing else
    touched) also makes the old path disappear, but it is not a deletion --
    every entry the base branch recorded is still there, just under
    `drizzle_dir` instead. Compare by (idx, tag, when), the same key
    `check_structure()`'s baseline lookup and `check_history()` use, rather
    than by path: only fail when a base entry is missing from the current
    journal everywhere, not merely missing at its old path.
    """
    if not base_dir or not base_entries or base_dir == drizzle_dir:
        return []
    if os.path.exists(journal_path(base_dir)):
        return []
    base_keys = {(e["idx"], e["tag"], e["when"]) for e in base_entries}
    current_keys = {(e["idx"], e["tag"], e["when"]) for e in (entries or [])}
    if base_keys <= current_keys:
        return []
    return [
        f"{journal_path(base_dir)} existed on {base_ref} with "
        f"{len(base_entries)} migration(s) recorded and does not exist on "
        "this branch, even though a Drizzle journal exists elsewhere in the "
        f"repository. Production has already applied them. {APPEND_ONLY_FIX}"
    ]


def report(errors, drizzle_dir=None):
    print("Drizzle journal check failed.\n")
    for error in errors:
        print(f"  - {error}\n")
    docs = [f"{drizzle_dir}/CLAUDE.md"] if drizzle_dir else []
    docs += ["CLAUDE.md", ".claude/CLAUDE.md", "AGENTS.md"]
    for doc in docs:
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
    # Discovery only ever runs below when this is empty, so it's the one
    # place that distinguishes "no --dir/DRIZZLE_DIR given at all" from "one
    # was given". Used later to tell a legitimate "no Drizzle in this repo"
    # apart from "the directory the caller pointed at has no journal in it",
    # which is a config error, not a no-op.
    explicit_dir = bool(drizzle_dir)
    if not drizzle_dir:
        drizzle_dir, error, ambiguous = discover_drizzle_dir()
        if error:
            # Ambiguous discovery (`ambiguous` is True) hits this branch too:
            # it cannot pick a directory to validate, and this is a
            # security-relevant gate whose whole job is catching tampering
            # with already-applied migrations, so it fails loud rather than
            # silently exiting 0 before any check has run. See the docstring
            # on discover_drizzle_dir() for why a quieter heuristic was
            # rejected. "Not a git repository" hits the same branch and is
            # just as much a reason nothing here can run.
            return report([error])

    # Resolve the base branch's merge-base (fork point) once, up front, and
    # anchor every base-branch question to that same commit: which directory
    # holds the journal there, and what it contained. Using two different
    # refs for those two questions (for example the base branch's moving tip
    # for the directory, and the merge-base for its content) lets a rename on
    # the base branch after the fork point desync them, so a real deletion or
    # tamper on the merge-base's actual directory would misread as "no
    # journal on the base branch" and skip the history checks instead of
    # failing. See discover_drizzle_dir_at()'s docstring.
    merge_base = None
    base_entries = None
    base_dir_at_merge_base = None
    base_entries_at_base_dir = None
    if args.base:
        merge_base = resolve_merge_base(args.base)
        if merge_base:
            base_dir_at_merge_base = discover_drizzle_dir_at(merge_base)
            if base_dir_at_merge_base:
                base_entries_at_base_dir = load_base_entries(
                    journal_path(base_dir_at_merge_base), merge_base, args.base
                )

            # `check_structure()` needs base_entries to know which baseline
            # entries were already on the base branch (a baseline entry only
            # excuses a violation that already merged, not one introduced in
            # the same commit), and `check_history()` needs the rest of it --
            # both for whatever directory is actually being validated
            # (`drizzle_dir`), which is not necessarily the same directory
            # the base branch used (see check_base_dir_not_deleted() for why
            # that mismatch matters on its own).
            lookup_dir = drizzle_dir or base_dir_at_merge_base
            if lookup_dir == base_dir_at_merge_base:
                base_entries = base_entries_at_base_dir
            elif lookup_dir:
                base_entries = load_base_entries(
                    journal_path(lookup_dir), merge_base, args.base
                )

            # Covers the journal being deleted wholesale with nothing new
            # added in its place: when the current tree has no journal at
            # all, fall back to where it lived on the base branch so the
            # checks below can tell "never existed" apart from "existed on
            # the base branch and is gone now".
            if not drizzle_dir and base_entries:
                drizzle_dir = lookup_dir

    if not drizzle_dir:
        print("No Drizzle journal in this repository, nothing to check.")
        return 0

    path = journal_path(drizzle_dir)
    file_exists = os.path.exists(path)
    if file_exists:
        with open(path, "r") as handle:
            entries, errors = load_journal(handle.read(), path)
    else:
        entries, errors = None, []
        if not base_entries:
            if explicit_dir:
                # Discovery was bypassed entirely -- the caller pointed
                # --dir/DRIZZLE_DIR at this exact path -- and there is no
                # journal there, and none on the base branch either, so this
                # is not "no Drizzle in this repo", it's a wrong or stale
                # override. Silently returning 0 here would mean an
                # explicit, misconfigured --dir validates nothing at all.
                return report([
                    f"{path} does not exist, but --dir/DRIZZLE_DIR "
                    f"explicitly set the migrations directory to "
                    f"{drizzle_dir}. Either that directory is wrong, or the "
                    "journal is missing from it."
                ])
            print(f"No {path}, nothing to check.")
            return 0
        # else: the base branch had a non-empty journal here and the current
        # tree has none. `entries` stays None; check_history() below reports
        # every base entry as removed.

    allowed, baseline_errors = load_baseline()
    errors += baseline_errors

    notes = []
    if entries is not None:
        base_keys = {
            (e["idx"], e["tag"], e["when"]) for e in (base_entries or [])
        }
        structural, notes = check_structure(entries, allowed, base_keys)
        errors += structural
        errors += check_files_match(entries, drizzle_dir)
    if args.base and (entries is not None or not file_exists):
        # Skip only when the current journal exists but failed to parse --
        # that error already explains itself, and reporting every base entry
        # as "removed" on top of it would be noise, not signal.
        errors += check_history(entries, drizzle_dir, args.base, merge_base, base_entries)
    if args.base:
        errors += check_base_dir_not_deleted(
            base_dir_at_merge_base,
            base_entries_at_base_dir,
            args.base,
            drizzle_dir,
            entries,
        )

    for note in notes:
        print(f"note: {note}")

    if errors:
        return report(errors, drizzle_dir)

    count = len(entries) if entries else 0
    print(
        f"Drizzle journal is fine: {count} migrations in {drizzle_dir}, "
        "correctly ordered."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
