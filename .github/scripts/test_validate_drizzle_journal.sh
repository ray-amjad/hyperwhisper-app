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

# ── result ───────────────────────────────────────────────────────────────────

echo
echo "passed $PASS, failed $FAIL"
cd /
rm -rf "$ROOT"
[ "$FAIL" = "0" ]
