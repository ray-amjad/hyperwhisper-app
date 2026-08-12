#!/bin/bash
# Tests for validate_drizzle_journal.py.
#
# Each case builds a throwaway git repository with a small journal, so the
# tests never touch this repository's real migrations. Run it after any edit
# to the check:
#
#   bash .github/scripts/test_validate_drizzle_journal.sh
#
# CI runs it too, so a change that breaks a rule fails the pull request that
# makes it rather than the pull request that trips over it later.
set -u

HERE=$(cd "$(dirname "$0")" && pwd)
SCRIPT=${1:-$HERE/validate_drizzle_journal.py}
ROOT=$(mktemp -d)
PASS=0
FAIL=0

# ── helpers ──────────────────────────────────────────────────────────────────

journal() { # reads "idx tag when" triples on stdin, writes journal + .sql files
  printf '{"version":"7","dialect":"postgresql","entries":[' \
    > drizzle/meta/_journal.json
  local first=1 idx tag when
  while read -r idx tag when; do
    [ -z "${idx:-}" ] && continue
    [ $first -eq 0 ] && printf ',' >> drizzle/meta/_journal.json
    first=0
    printf '{"idx":%s,"version":"7","when":%s,"tag":"%s","breakpoints":true}' \
      "$idx" "$when" "$tag" >> drizzle/meta/_journal.json
    echo "-- $tag" > "drizzle/$tag.sql"
  done
  printf ']}\n' >> drizzle/meta/_journal.json
}

fresh() { # repo whose main branch already has three applied migrations
  rm -rf "$ROOT/repo"
  mkdir -p "$ROOT/repo/drizzle/meta" "$ROOT/repo/.github"
  cd "$ROOT/repo" || exit 1
  git init -q -b main .
  git config user.email test@example.com
  git config user.name test
  journal <<'EOF'
0 0000_alpha 1000
1 0001_bravo 2000
2 0002_charlie 3000
EOF
  git add -A && git commit -qm base
  git checkout -qb feature
}

want() { # want <name> <expected exit> [substring the output must contain]
  local name="$1" expected="$2" needle="${3:-}" out got
  out=$(BASE_REF=main python3 "$SCRIPT" 2>&1)
  got=$?
  if [ "$got" != "$expected" ]; then
    echo "FAIL $name: exit $got, expected $expected"
    echo "$out" | sed 's/^/       /'
    FAIL=$((FAIL + 1))
    return
  fi
  if [ -n "$needle" ] && ! echo "$out" | grep -q "$needle"; then
    echo "FAIL $name: output did not mention '$needle'"
    echo "$out" | sed 's/^/       /'
    FAIL=$((FAIL + 1))
    return
  fi
  echo "ok   $name"
  PASS=$((PASS + 1))
}

# ── the journal on its own, and against its base branch ──────────────────────

fresh
want "untouched branch" 0

fresh
journal <<'EOF'
0 0000_alpha 1000
1 0001_bravo 2000
2 0002_charlie 3000
3 0003_delta 4000
EOF
git add -A && git commit -qm append
want "correct append" 0

fresh
journal <<'EOF'
0 0000_alpha 1000
1 0001_bravo 2000
2 0002_charlie 3000
3 0003_delta 2500
EOF
git add -A && git commit -qm past
want "new entry stamped in the past" 1 "not past the newest entry"

fresh
journal <<'EOF'
0 0000_alpha 1000
1 0001_bravo 2500
2 0002_charlie 3000
EOF
git add -A && git commit -qm restamp
want "re-stamped an applied entry" 1 'changed `when`'

fresh
journal <<'EOF'
0 0000_alpha 1000
1 0001_bravo 2000
EOF
git add -A && git commit -qm remove
want "removed an applied entry" 1 "was removed"

fresh
echo "-- tampered" >> drizzle/0001_bravo.sql
git add -A && git commit -qm edit
want "edited an applied .sql" 1 "was edited"

fresh
git mv drizzle/0001_bravo.sql drizzle/0001_renamed.sql
journal <<'EOF'
0 0000_alpha 1000
1 0001_renamed 2000
2 0002_charlie 3000
EOF
git add -A && git commit -qm rename
want "renamed an applied migration" 1 'changed `tag`'

fresh
echo "-- stray" > drizzle/0009_orphan.sql
git add -A && git commit -qm orphan
want "orphan NNNN_ file" 1 "no journal entry lists them"

fresh
echo "-- hand run" > drizzle/custom_triggers.sql
git add -A && git commit -qm helper
want "hand-run helper added" 0

fresh
echo "-- hand run" > drizzle/custom_triggers.sql
git add -A && git commit -qm helper
echo "-- changed" >> drizzle/custom_triggers.sql
git add -A && git commit -qm "edit helper"
want "hand-run helper edited" 0

# One entry stamped in the future sits above several later ones at once. A
# neighbour-only comparison would report the first victim and hide the rest.
fresh
journal <<'EOF'
0 0000_alpha 1000
1 0001_bravo 2000
2 0002_charlie 3000
3 0003_delta 9000
4 0004_echo 4000
5 0005_foxtrot 5000
EOF
git add -A && git commit -qm future
out=$(BASE_REF=main python3 "$SCRIPT" 2>&1)
count=$(echo "$out" | grep -c "does not come after")
if [ "$count" = "2" ]; then
  echo "ok   a future stamp hides both later entries"
  PASS=$((PASS + 1))
else
  echo "FAIL a future stamp hides both later entries: reported $count, wanted 2"
  echo "$out" | sed 's/^/       /'
  FAIL=$((FAIL + 1))
fi

# ── the baseline file, for violations already merged and reconciled ──────────

with_history() { # main already carries an out-of-order pair at idx 3
  rm -rf "$ROOT/repo"
  mkdir -p "$ROOT/repo/drizzle/meta" "$ROOT/repo/.github"
  cd "$ROOT/repo" || exit 1
  git init -q -b main .
  git config user.email test@example.com
  git config user.name test
  journal <<'EOF'
0 0000_alpha 1000
1 0001_bravo 2000
2 0002_charlie 9000
3 0003_delta 3000
EOF
  git add -A && git commit -qm base
  git checkout -qb feature
}

baseline() { # write the baseline file from stdin and commit it
  cat > .github/drizzle-journal-baseline.json
  git add -A && git commit -qm baseline
}

with_history
want "pre-existing violation fails without a baseline" 1 "does not come after"

with_history
baseline <<'EOF'
{
  "allow_out_of_order": [
    {
      "idx": 3,
      "tag": "0003_delta",
      "when": 3000,
      "why": "Verified applied in production, 4 of 4 rows recorded."
    }
  ]
}
EOF
want "baseline accepts the recorded violation" 0 "accepted by"

with_history
baseline <<'EOF'
{"allow_out_of_order":[{"idx":3,"tag":"0003_delta","when":3000,"why":"checked"}]}
EOF
python3 - <<'EOF'
import json
path = "drizzle/meta/_journal.json"
data = json.load(open(path))
data["entries"][3]["when"] = 3500
json.dump(data, open(path, "w"))
EOF
git add -A && git commit -qm restamp
want "baseline does not cover a re-stamped entry" 1 'changed `when`'

with_history
baseline <<'EOF'
{"allow_out_of_order":[{"idx":3,"tag":"0003_WRONG","when":3000,"why":"checked"}]}
EOF
want "baseline with the wrong tag does not match" 1 "does not come after"

with_history
baseline <<'EOF'
{"allow_out_of_order":[{"idx":3,"tag":"0003_delta","when":3000}]}
EOF
want "baseline entry with no reason is rejected" 1 'needs `idx`'

# A baseline entry only excuses a violation already on the base branch. A
# pull request that adds a genuinely new out-of-order entry AND a baseline
# entry excusing it, in the same commit, must still fail -- otherwise the
# baseline file is a self-service bypass.
fresh
journal <<'EOF'
0 0000_alpha 1000
1 0001_bravo 2000
2 0002_charlie 3000
3 0003_delta 2500
EOF
printf '%s' '{"allow_out_of_order":[{"idx":3,"tag":"0003_delta","when":2500,"why":"self-approved"}]}' \
  > .github/drizzle-journal-baseline.json
git add -A && git commit -qm "new violation plus a same-commit baseline entry"
want "a same-commit baseline entry cannot excuse a new violation" 1 "does not come after"

# ── deleting the journal wholesale ────────────────────────────────────────────

fresh
git rm -rq drizzle
git commit -qm "delete the journal and every migration"
want "deleting the whole journal when the base had entries fails" 1 "was removed"

# ── symlinking over an applied migration ──────────────────────────────────────

fresh
rm drizzle/0001_bravo.sql
ln -s /etc/passwd drizzle/0001_bravo.sql
git add -A && git commit -qm "swap an applied migration for a symlink"
want "symlinking over an applied migration fails like editing it" 1 "was edited"

# ── a dangling symlink or directory cannot masquerade as a migration file ────
#
# Round 3: os.listdir() alone can't tell a real .sql file from a dangling
# symlink or a directory that merely has the right name. Either would count
# as "on disk" and mask a migration the journal expects but that doesn't
# actually exist as a runnable file.

fresh
journal <<'EOF'
0 0000_alpha 1000
1 0001_bravo 2000
2 0002_charlie 3000
3 0003_delta 4000
EOF
rm drizzle/0003_delta.sql
ln -s /nonexistent-target drizzle/0003_delta.sql
git add -A && git commit -qm "a dangling symlink stands in for a new migration's SQL file"
want "a dangling symlink does not count as the migration's SQL file" 1 "no SQL file"

fresh
journal <<'EOF'
0 0000_alpha 1000
1 0001_bravo 2000
2 0002_charlie 3000
3 0003_delta 4000
EOF
rm drizzle/0003_delta.sql
mkdir drizzle/0003_delta.sql
touch drizzle/0003_delta.sql/placeholder
git add -A && git commit -qm "a directory stands in for a new migration's SQL file"
want "a directory named like a migration's SQL file does not count as present" 1 "no SQL file"

# ── a silently-swallowed git failure must not read as \"nothing changed\" ─────

# Shadow `git` so only `git diff --name-status` fails; merge-base, ls-files
# and show still run for real, so the rest of the check behaves normally and
# only the file-status step is exercised.
fresh
journal <<'EOF'
0 0000_alpha 1000
1 0001_bravo 2000
2 0002_charlie 3000
3 0003_delta 4000
EOF
git add -A && git commit -qm append
FAKEGIT_DIR=$(mktemp -d)
cat > "$FAKEGIT_DIR/git" <<WRAP
#!/bin/bash
if [ "\$1" = "diff" ] && [ "\$2" = "--name-status" ]; then
  exit 1
fi
exec $(command -v git) "\$@"
WRAP
chmod +x "$FAKEGIT_DIR/git"
out=$(PATH="$FAKEGIT_DIR:$PATH" BASE_REF=main python3 "$SCRIPT" 2>&1)
got=$?
rm -rf "$FAKEGIT_DIR"
if [ "$got" = "0" ] && echo "$out" | grep -qi "note:.*diff"; then
  echo "ok   a failed git diff is reported, not silently treated as no changes"
  PASS=$((PASS + 1))
else
  echo "FAIL a failed git diff is reported, not silently treated as no changes: exit $got"
  echo "$out" | sed 's/^/       /'
  FAIL=$((FAIL + 1))
fi

# ── an ambiguous journal must fail loud, not silently skip everything ────────
#
# Round 1 turned "more than one journal found" into "print a note and exit 0
# before any check runs" -- including on whatever journal(s) genuinely exist.
# That is a full bypass: dropping one throwaway journal-shaped file anywhere
# in the repo silences every check, structural or historical, repo-wide.

fresh
mkdir -p another/drizzle/meta
cp drizzle/meta/_journal.json another/drizzle/meta/_journal.json
git add -A && git commit -qm "a second Drizzle journal appears elsewhere"
want "a second journal anywhere fails loud instead of silently skipping" 1 "DRIZZLE_DIR"

fresh
echo "-- tampered" >> drizzle/0001_bravo.sql
mkdir -p another/drizzle/meta
cp drizzle/meta/_journal.json another/drizzle/meta/_journal.json
git add -A && git commit -qm "tamper with an applied migration and drop a decoy journal elsewhere"
out=$(BASE_REF=main python3 "$SCRIPT" 2>&1)
got=$?
if [ "$got" != "0" ]; then
  echo "ok   a decoy journal cannot mask a tampered SQL file as a successful run"
  PASS=$((PASS + 1))
else
  echo "FAIL a decoy journal cannot mask a tampered SQL file as a successful run: exit 0"
  echo "$out" | sed 's/^/       /'
  FAIL=$((FAIL + 1))
fi

# An explicit DRIZZLE_DIR bypasses discovery entirely, so ambiguity elsewhere
# in the repo must not stop the real directory from being validated.
fresh
echo "-- tampered" >> drizzle/0001_bravo.sql
mkdir -p another/drizzle/meta
cp drizzle/meta/_journal.json another/drizzle/meta/_journal.json
git add -A && git commit -qm "tamper plus a decoy, but DRIZZLE_DIR is set explicitly"
out=$(BASE_REF=main DRIZZLE_DIR=drizzle python3 "$SCRIPT" 2>&1)
got=$?
if [ "$got" = "1" ] && echo "$out" | grep -q "was edited"; then
  echo "ok   an explicit DRIZZLE_DIR bypasses discovery and still catches tampering"
  PASS=$((PASS + 1))
else
  echo "FAIL an explicit DRIZZLE_DIR bypasses discovery and still catches tampering: exit $got"
  echo "$out" | sed 's/^/       /'
  FAIL=$((FAIL + 1))
fi

# ── deleting the real journal while adding an unrelated decoy elsewhere ──────
#
# Discovery only looks at what exists right now. Deleting the real journal
# and adding one new, unrelated journal-shaped file elsewhere leaves exactly
# one match on HEAD (the decoy), so the "discovery found zero, fall back to
# the base branch" path never fires and the deletion goes unnoticed unless
# the base branch's own directory is checked directly.

fresh
git rm -rq drizzle
mkdir -p another/drizzle/meta
cat > another/drizzle/meta/_journal.json <<'EOF'
{"version":"7","dialect":"postgresql","entries":[{"idx":0,"version":"7","when":1000,"tag":"0000_only","breakpoints":true}]}
EOF
echo "-- only" > another/drizzle/0000_only.sql
git add -A && git commit -qm "delete the real journal, add an unrelated decoy journal elsewhere"
want "deleting the real journal while adding a decoy elsewhere still fails" 1 "existed on"

# ── a base-branch rename after the fork point must not blind history checks ──
#
# discover_drizzle_dir_at() used to resolve the base directory at the base
# branch's moving tip while the journal content was loaded at the
# merge-base. If the base branch renames the migrations directory after the
# pull request's fork point, those two refs disagree and the base-branch
# fallback silently finds "no journal" instead of resolving correctly.

fresh_rename_after_fork() { # main renames the migrations dir after feature forked
  rm -rf "$ROOT/repo"
  mkdir -p "$ROOT/repo/drizzle/meta" "$ROOT/repo/.github"
  cd "$ROOT/repo" || exit 1
  git init -q -b main .
  git config user.email test@example.com
  git config user.name test
  journal <<'EOF'
0 0000_alpha 1000
1 0001_bravo 2000
2 0002_charlie 3000
EOF
  git add -A && git commit -qm base
  git checkout -qb feature
  git checkout -q main
  git mv drizzle drizzle2
  git add -A && git commit -qm "rename the migrations directory on main after the fork"
  git checkout -q feature
}

fresh_rename_after_fork
git rm -rq drizzle
git commit -qm "delete the journal entirely on the feature branch"
want "a base-branch rename after the fork does not blind deletion detection" 1 "was removed"

# ── a pure content-identical rename of the whole migrations directory ────────
#
# Round 3: check_base_dir_not_deleted() used to key "deleted" purely off
# os.path.exists() on the base branch's OLD journal path. A `git mv drizzle
# db/drizzle` with nothing else touched makes that old path vanish even
# though every entry is still there, just relocated -- that's a rename, not
# a deletion, and must not fail forever with no escape hatch.

fresh
mkdir -p db
git mv drizzle db/drizzle
git add -A && git commit -qm "rename the whole migrations directory, content unchanged"
want "a pure directory rename does not read as a deletion" 0

fresh
mkdir -p db
git mv drizzle db/drizzle
git add -A && git commit -qm "rename the whole migrations directory, content unchanged"
out=$(BASE_REF=main DRIZZLE_DIR=db/drizzle python3 "$SCRIPT" 2>&1)
got=$?
if [ "$got" = "0" ]; then
  echo "ok   an explicit --dir/DRIZZLE_DIR at the new location also sees the rename, not a deletion"
  PASS=$((PASS + 1))
else
  echo "FAIL an explicit --dir/DRIZZLE_DIR at the new location also sees the rename, not a deletion: exit $got"
  echo "$out" | sed 's/^/       /'
  FAIL=$((FAIL + 1))
fi

# A rename must not become a laundering trick for an actual removal: if an
# applied entry is dropped from the journal in the same commit that moves
# the directory, that is still a deletion and must still fail.
fresh
mkdir -p db
git mv drizzle db/drizzle
python3 - <<'EOF'
import json
path = "db/drizzle/meta/_journal.json"
data = json.load(open(path))
data["entries"] = [e for e in data["entries"] if e["idx"] != 1]
json.dump(data, open(path, "w"))
EOF
rm -f db/drizzle/0001_bravo.sql
git add -A && git commit -qm "rename the directory AND drop one applied migration"
# check_history() can't see this (the new directory didn't exist yet at the
# merge-base, so it has nothing to diff against there), but
# check_base_dir_not_deleted() still catches it directly by content.
want "a rename that also drops an applied entry still fails" 1 "already applied them"

# ── two files claiming the same numeric prefix, even with distinct tags ──────
#
# drizzle-kit sorts by the four-digit prefix alone. A merge conflict resolved
# by hand can give two colliding files different idx/tag values in the
# journal, so the idx/tag duplicate checks above do not catch this -- it used
# to be a separate `ls | cut -d_ -f1 | uniq -d` step in ci.yml (#1041 vs
# #1045), removed on the assumption this script covers it.

fresh
journal <<'EOF'
0 0000_alpha 1000
1 0001_bravo 2000
2 0002_charlie 3000
3 0003_delta 4000
4 0003_deltaprime 5000
EOF
git add -A && git commit -qm "two files claim the same numeric prefix"
want "duplicate numeric prefix across files with distinct tags fails" 1 "prefix 0003"

# ── an explicit --dir/DRIZZLE_DIR pointing at nothing must fail loud ─────────
#
# Round 3: when args.dir bypasses discovery but that directory has no
# journal in it, the check fell through to the same "nothing to check, exit
# 0" path used for a repo with no Drizzle at all. A misconfigured or stale
# override silently validated nothing.

fresh
out=$(BASE_REF=main DRIZZLE_DIR=does/not/exist python3 "$SCRIPT" 2>&1)
got=$?
if [ "$got" = "1" ] && echo "$out" | grep -q "does/not/exist"; then
  echo "ok   an explicit DRIZZLE_DIR with no journal there fails loud, not silently"
  PASS=$((PASS + 1))
else
  echo "FAIL an explicit DRIZZLE_DIR with no journal there fails loud, not silently: exit $got"
  echo "$out" | sed 's/^/       /'
  FAIL=$((FAIL + 1))
fi

fresh
out=$(DRIZZLE_DIR=does/not/exist python3 "$SCRIPT" 2>&1)
got=$?
if [ "$got" = "1" ] && echo "$out" | grep -q "does/not/exist"; then
  echo "ok   an explicit DRIZZLE_DIR with no journal and no --base still fails loud"
  PASS=$((PASS + 1))
else
  echo "FAIL an explicit DRIZZLE_DIR with no journal and no --base still fails loud: exit $got"
  echo "$out" | sed 's/^/       /'
  FAIL=$((FAIL + 1))
fi

# The legitimate case -- no --dir given, and discovery genuinely finds no
# Drizzle journal anywhere -- must still exit 0. This is the boundary the fix
# above has to respect: only an *explicit* override with nothing there is a
# config error, not "no Drizzle in this repo at all".
fresh
git rm -rq drizzle
git commit -qm "no Drizzle in this repo at all"
out=$(python3 "$SCRIPT" 2>&1)
got=$?
if [ "$got" = "0" ]; then
  echo "ok   no --dir and nothing discovered still exits 0, not a config error"
  PASS=$((PASS + 1))
else
  echo "FAIL no --dir and nothing discovered still exits 0, not a config error: exit $got"
  echo "$out" | sed 's/^/       /'
  FAIL=$((FAIL + 1))
fi

# NOTE: drizzle-journal.yml itself (push trigger for main + conditional
# BASE_REF) is not in this push -- the bot token can't push
# .github/workflows/* changes (GitHub withholds `workflows` permission from
# GitHub App tokens). That diff, plus the two structural tests that assert
# on it, are posted separately as a patch for a human to apply by hand; once
# applied, re-add the "workflow triggers on push to main" / "BASE_REF only
# resolves github.base_ref on pull_request events" checks here.

# ── the failure message points at the real doc next to the migrations ────────

fresh
echo "# Editing a migration" > drizzle/CLAUDE.md
journal <<'EOF'
0 0000_alpha 1000
1 0001_bravo 2000
EOF
git add -A && git commit -qm remove
want "the failure message points at the migrations directory's own CLAUDE.md" 1 "drizzle/CLAUDE.md"

# ── result ───────────────────────────────────────────────────────────────────

echo
echo "passed $PASS, failed $FAIL"
cd /
rm -rf "$ROOT"
[ "$FAIL" = "0" ]
