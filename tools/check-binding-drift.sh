#!/usr/bin/env bash
# Guard the committed UniFFI bindings against drift and re-duplication.
#
# `shared-core-rs/bindings/` is the ONLY place a generated binding is authored.
# Anything else in the tree that carries a generated binding file is a copy, and
# a copy that stops matching its source is silent, shipping breakage: the Rust
# FFI checksums only fail at the first FFI call, on whichever platform happened
# to keep the stale copy.
#
# Two rules, both enforced here:
#
#   1. NO copy of the C# binding may exist outside shared-core-rs/bindings/csharp/.
#      Every .NET head compiles the shared file through HyperWhisper.SharedCore.
#      Windows used to vendor its own copy under
#      app/windows/HyperWhisper/Generated/RustCore/; that put two sets of
#      `namespace uniffi.hyperwhisper_core` types in one process, each assembly
#      keeping its own UniFFI callback-vtable handle map (issue #275).
#
#   2. Every OTHER committed copy must be byte-identical to its source in
#      shared-core-rs/bindings/. Xcode compiles the Swift binding from inside the
#      app target, so the macOS copies stay — they just may not drift.
#
# Usage:
#   tools/check-binding-drift.sh          # check; non-zero exit on any violation
#   tools/check-binding-drift.sh --fix    # refresh the allowed copies from source
#
# Only tracked files are considered, so build output and local scratch copies are
# ignored.
set -euo pipefail

cd "$(dirname "$0")/.."

SOURCE_DIR="shared-core-rs/bindings"

FIX=0
if [[ "${1:-}" == "--fix" ]]; then
  FIX=1
elif [[ $# -gt 0 ]]; then
  echo "usage: $0 [--fix]" >&2
  exit 2
fi

# Copies that are allowed to exist, as "<copy path>:<source path>". Xcode's
# project.pbxproj references these paths directly, which is why they are vendored
# rather than linked.
ALLOWED_COPIES=(
  "app/macos/hyperwhisper/RustCore/hyperwhisper_core.swift:$SOURCE_DIR/swift/hyperwhisper_core.swift"
  "app/macos/hyperwhisper/Libraries/hyperwhisper_coreFFI.h:$SOURCE_DIR/swift/hyperwhisper_coreFFI.h"
)

# Every filename the generator emits. A new binding language must be added here,
# otherwise its copies are invisible to this check.
GENERATED_BASENAMES=(
  "hyperwhisper_core.cs"
  "hyperwhisper_core.swift"
  "hyperwhisper_core.kt"
  "hyperwhisper_coreFFI.h"
  "hyperwhisper_coreFFI.modulemap"
)

failures=0
fail() {
  printf 'FAIL: %s\n' "$1" >&2
  failures=$((failures + 1))
}

allowed_source_for() {
  local candidate="$1" entry
  for entry in "${ALLOWED_COPIES[@]}"; do
    if [[ "${entry%%:*}" == "$candidate" ]]; then
      printf '%s' "${entry#*:}"
      return 0
    fi
  done
  return 1
}

# ---------------------------------------------------------------------------
# Rule 1 + 2: find every tracked generated-binding file outside the source dir.
# ---------------------------------------------------------------------------
pattern=""
for base in "${GENERATED_BASENAMES[@]}"; do
  pattern+="${pattern:+|}$(printf '%s' "$base" | sed 's/\./\\./g')"
done

mapfile -t copies < <(
  git ls-files -z \
    | tr '\0' '\n' \
    | grep -E "(^|/)($pattern)$" \
    | grep -v "^$SOURCE_DIR/" \
    | sort
)

for copy in "${copies[@]}"; do
  [[ -n "$copy" ]] || continue

  if [[ "$copy" == *.cs ]]; then
    fail "$copy is a vendored copy of the C# binding. The C# binding compiles
      once, in app/shared-dotnet/HyperWhisper.SharedCore, from
      $SOURCE_DIR/csharp/hyperwhisper_core.cs. Delete this file and reference
      HyperWhisper.SharedCore instead — see issue #275."
    continue
  fi

  if ! source_path="$(allowed_source_for "$copy")"; then
    fail "$copy is an unknown copy of a generated binding. Either consume
      $SOURCE_DIR/ directly, or add the copy to ALLOWED_COPIES in $0 with the
      source it must track."
    continue
  fi

  if [[ ! -f "$source_path" ]]; then
    fail "$copy claims to track $source_path, which does not exist."
    continue
  fi

  if cmp -s "$copy" "$source_path"; then
    continue
  fi

  if [[ $FIX -eq 1 ]]; then
    cp "$source_path" "$copy"
    echo "fixed: $copy <- $source_path"
    continue
  fi

  fail "$copy has drifted from $source_path. Regenerate the bindings
      (shared-core-rs/build-bindings.sh) and run '$0 --fix', then commit both."
  diff -u "$source_path" "$copy" | head -40 >&2 || true
done

# ---------------------------------------------------------------------------
# Rule 3: every allowed copy must still be present. A copy silently disappearing
# from the Xcode tree is drift too — the app would compile against a stale file
# still referenced by project.pbxproj, or fail confusingly.
# ---------------------------------------------------------------------------
for entry in "${ALLOWED_COPIES[@]}"; do
  copy="${entry%%:*}"
  if ! git ls-files --error-unmatch "$copy" >/dev/null 2>&1; then
    fail "$copy is listed in ALLOWED_COPIES but is not tracked. Restore it, or
      drop the entry from $0."
  fi
done

if [[ $failures -gt 0 ]]; then
  echo >&2
  echo "$failures binding-copy problem(s) found." >&2
  exit 1
fi

echo "OK: ${#copies[@]} committed binding copy/copies match $SOURCE_DIR/, and no C# copy exists."
