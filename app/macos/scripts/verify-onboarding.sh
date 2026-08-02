#!/usr/bin/env bash
#
# verify-onboarding.sh
#
# Reusable gate for the macOS onboarding flow.
#
#   1. Builds the app and runs ONLY the hyperwhisperTests unit suite.
#   2. Static design conformance over the onboarding views.
#   3. Localization checks (no unlocalized Text, no missing keys).
#   4. Regression guards for the four known onboarding bugs.
#   5. Repo hygiene: no prototype references, no hardcoded signing identity.
#
# Signing: DEVELOPMENT_TEAM is read from the environment and is NEVER hardcoded
# here. This repo deliberately commits an empty team. Run it like:
#
#   DEVELOPMENT_TEAM=YOURTEAM app/macos/scripts/verify-onboarding.sh
#
# Exit codes:
#   0   build + unit tests passed and every hard check passed
#   1   a hard check failed, or the build / unit tests failed
#   2   unit tests could not be run at all (no signing team in the environment)
#   64  usage error
#
# UI tests are deliberately NOT run: the XCUITest runner is rejected by macOS
# container consent on this machine before any test code executes. This script
# never claims UI test coverage.

set -uo pipefail

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------

SCRIPT_PATH="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/$(basename "${BASH_SOURCE[0]}")"
SCRIPTS_DIR="$(dirname "$SCRIPT_PATH")"
MACOS_DIR="$(cd "$SCRIPTS_DIR/.." && pwd)"
REPO_ROOT="$(cd "$MACOS_DIR/../.." && pwd)"

PROJECT="$MACOS_DIR/hyperwhisper.xcodeproj"
SCHEME="hyperwhisper"
APP_SRC="$MACOS_DIR/hyperwhisper"
TESTS_DIR="$MACOS_DIR/hyperwhisperTests"
VIEWS_DIR="$APP_SRC/Views"
ONBOARDING_VIEW="$VIEWS_DIR/OnboardingView.swift"
ONBOARDING_DIR="$VIEWS_DIR/Onboarding"
LOC_DIR="$APP_SRC/Localizations"
BASE_STRINGS="$LOC_DIR/Base.lproj/Localizable.strings"
PROTOTYPE_NAME="macos-onboarding-prototype"

DERIVED_DATA="${HW_DERIVED_DATA:-/tmp/hw-macos-derived}"
RUN_BUILD=1
RUN_TESTS=1

TMP="$(mktemp -d "${TMPDIR:-/tmp}/verify-onboarding.XXXXXX")"
trap 'rm -rf "$TMP"' EXIT

BUILD_LOG="$TMP/build.log"
TEST_LOG="$TMP/test.log"
RESULT_BUNDLE="$TMP/tests.xcresult"

EM_DASH="$(printf '\xe2\x80\x94')"

# ---------------------------------------------------------------------------
# Reporting
# ---------------------------------------------------------------------------

HARD_FAILS=0
WARNS=0
PASSES=0
BLOCKED=0
FAIL_LINES="$TMP/failures.txt"
: > "$FAIL_LINES"

if [ -t 1 ]; then
    C_RED="$(printf '\033[31m')"; C_GRN="$(printf '\033[32m')"
    C_YEL="$(printf '\033[33m')"; C_DIM="$(printf '\033[2m')"
    C_BLD="$(printf '\033[1m')";  C_OFF="$(printf '\033[0m')"
else
    C_RED=""; C_GRN=""; C_YEL=""; C_DIM=""; C_BLD=""; C_OFF=""
fi

pass()  { PASSES=$((PASSES + 1)); printf '%s[PASS]%s %s\n' "$C_GRN" "$C_OFF" "$1"; }
fail()  { HARD_FAILS=$((HARD_FAILS + 1)); printf '%s[FAIL]%s %s\n' "$C_RED" "$C_OFF" "$1"; echo "$1" >> "$FAIL_LINES"; }
warn()  { WARNS=$((WARNS + 1)); printf '%s[WARN]%s %s\n' "$C_YEL" "$C_OFF" "$1"; }
info()  { printf '%s[INFO]%s %s\n' "$C_DIM" "$C_OFF" "$1"; }
note()  { printf '%s[NOTE]%s %s\n' "$C_DIM" "$C_OFF" "$1"; }
head1() { printf '\n%s== %s ==%s\n' "$C_BLD" "$1" "$C_OFF"; }
detail() { printf '        %s\n' "$1"; }

# Print up to N matching evidence lines, indented, as "file:line: text".
show_hits() {
    local file="$1" limit="${2:-8}" n=0
    while IFS= read -r l; do
        [ -z "$l" ] && continue
        n=$((n + 1))
        [ "$n" -gt "$limit" ] && { detail "... (more suppressed)"; break; }
        detail "$(printf '%s' "$l" | cut -c1-200)"
    done < "$file"
}

usage() {
    sed -n '3,28p' "$SCRIPT_PATH" | sed 's/^# \{0,1\}//'
    echo
    echo "Usage: verify-onboarding.sh [--static-only] [--no-tests] [--help]"
    echo "  --static-only  skip the build and the unit tests (never exits 0)"
    echo "  --no-tests     build, but skip the unit test run (never exits 0)"
}

while [ $# -gt 0 ]; do
    case "$1" in
        --static-only) RUN_BUILD=0; RUN_TESTS=0 ;;
        --no-tests)    RUN_TESTS=0 ;;
        -h|--help)     usage; exit 0 ;;
        *) echo "unknown argument: $1" >&2; usage >&2; exit 64 ;;
    esac
    shift
done

# ---------------------------------------------------------------------------
# File sets
# ---------------------------------------------------------------------------

# Design surface named by the brief: OnboardingView.swift + Views/Onboarding/.
DESIGN_FILES="$TMP/design_files.txt"
: > "$DESIGN_FILES"
[ -f "$ONBOARDING_VIEW" ] && echo "$ONBOARDING_VIEW" >> "$DESIGN_FILES"
[ -d "$ONBOARDING_DIR" ] && find "$ONBOARDING_DIR" -name '*.swift' -type f >> "$DESIGN_FILES"

# Wider onboarding surface: anything in the app target named *Onboarding*.swift
# (covers a ported view model living outside Views/).
ALL_ONB="$TMP/all_onboarding_swift.txt"
{
    cat "$DESIGN_FILES"
    find "$APP_SRC" -name '*Onboarding*.swift' -type f
} 2>/dev/null | sort -u > "$ALL_ONB"

design_files_args() { tr '\n' '\0' < "$DESIGN_FILES" | xargs -0 "$@" 2>/dev/null; }
all_onb_args()      { tr '\n' '\0' < "$ALL_ONB"      | xargs -0 "$@" 2>/dev/null; }

DESIGN_COUNT=$(wc -l < "$DESIGN_FILES" | tr -d ' ')
ALL_ONB_COUNT=$(wc -l < "$ALL_ONB" | tr -d ' ')

# Locate the microphone step. Two files can be involved: the one that OWNS the
# device list (may be a view model) and the one that RENDERS it. Ordering is
# checked against the owner; the one-line device name and the Sound Settings
# button are checked against the renderer.
MIC_FILE=""
MIC_VIEW_FILE=""
if [ "$ALL_ONB_COUNT" -gt 0 ]; then
    MIC_FILE=$(all_onb_args grep -lE 'availableDevices|selectedDevice|inputDevice|onboarding\.mic\.|onboarding\.audio\.' | head -1)
    MIC_VIEW_FILE=$(all_onb_args grep -lE 'struct[[:space:]]+[A-Za-z]*(Microphone|Mic)[A-Za-z]*(View|Step|Card)[[:space:]]*:[[:space:]]*View' | head -1)
    [ -z "$MIC_VIEW_FILE" ] && MIC_VIEW_FILE="$MIC_FILE"
fi

# Extract "name|start|end" for every Swift func in a file (brace balanced,
# strings and // comments stripped first).
extract_funcs() {
    awk '
    {
        line = $0
        sub(/\/\/.*/, "", line)
        gsub(/"[^"]*"/, "\"\"", line)
        if (tracking == 0) {
            if (match(line, /(^|[^A-Za-z0-9_])func[ \t]+[A-Za-z_][A-Za-z0-9_]*/)) {
                s = substr(line, RSTART, RLENGTH)
                sub(/^.*func[ \t]+/, "", s)
                name = s; tracking = 1; start = NR; depth = 0; opened = 0
            }
        }
        if (tracking == 1) {
            n = gsub(/\{/, "{", line)
            m = gsub(/\}/, "}", line)
            depth += n - m
            if (n > 0) opened = 1
            if (opened == 1 && depth <= 0) { print name "|" start "|" NR; tracking = 0 }
        }
    }' "$1"
}

printf '%s\n' "${C_BLD}HyperWhisper onboarding gate${C_OFF}"
info "repo:          $REPO_ROOT"
info "design files:  $DESIGN_COUNT (OnboardingView.swift + Views/Onboarding/)"
info "onboarding sw: $ALL_ONB_COUNT files scanned for localization / regressions"
[ -n "$MIC_FILE" ] && info "microphone ui: ${MIC_FILE#$REPO_ROOT/}"

if [ "$DESIGN_COUNT" -eq 0 ]; then
    fail "no onboarding view files found (expected $ONBOARDING_VIEW and/or $ONBOARDING_DIR/)"
fi

# ===========================================================================
head1 "1. Repo hygiene"
# ===========================================================================

# --- 5. no references to the standalone prototype -------------------------
HITS="$TMP/proto.txt"
grep -rIn --binary-files=without-match \
    --exclude-dir=DerivedData --exclude-dir=.build --exclude-dir=build \
    --exclude="$(basename "$SCRIPT_PATH")" \
    -E "$PROTOTYPE_NAME|OnboardingPrototype|PrototypeStyle|PrototypeProvider|PrototypeModel" \
    "$MACOS_DIR" > "$HITS" 2>/dev/null
if [ -s "$HITS" ]; then
    fail "app/macos references the standalone onboarding prototype (it is being deleted; port, do not import)"
    show_hits "$HITS" 10
else
    pass "no references to the onboarding prototype anywhere under app/macos"
fi

# --- 6. no hardcoded signing identity under app/macos/scripts --------------
# An Apple team id is exactly 10 chars of [A-Z0-9] with both digits and letters,
# which is rare enough in shell to detect reliably. Referencing the team through
# the environment (${DEVELOPMENT_TEAM:-}) is the required, allowed form.
#
# Reads file:line:text on stdin, emits only lines carrying a team-id-shaped
# token (word bounded, at least 2 digits and 2 letters).
team_shaped() {
    awk '
    {
        rest = $0
        off = 0
        while (match(rest, /[A-Z0-9]{10}/)) {
            s = RSTART; l = RLENGTH
            tok = substr(rest, s, l)
            before = (s > 1) ? substr(rest, s - 1, 1) : " "
            after  = substr(rest, s + l, 1)
            if (before !~ /[A-Za-z0-9_]/ && after !~ /[A-Za-z0-9_]/) {
                d = tok; nd = gsub(/[0-9]/, "", d)
                a = tok; na = gsub(/[A-Z]/, "", a)
                if (nd >= 2 && na >= 2) { print $0; next }
            }
            rest = substr(rest, s + 1)
        }
    }'
}

HARD_SIGN="$TMP/signing_hard.txt"; : > "$HARD_SIGN"
SOFT_SIGN="$TMP/signing_soft.txt"; : > "$SOFT_SIGN"
if [ -d "$SCRIPTS_DIR" ]; then
    # Team-id-shaped token on a line that is talking about signing.
    grep -rIn --binary-files=without-match -iE 'team|sign|identity|notariz|keychain|provision|codesign' \
        "$SCRIPTS_DIR" 2>/dev/null | team_shaped >> "$HARD_SIGN"
    # A literal certificate common name.
    grep -rIn --binary-files=without-match \
        -E 'Developer ID (Application|Installer):[[:space:]]*[A-Za-z]|Apple Development:[[:space:]]*[A-Za-z]|Apple Distribution:[[:space:]]*[A-Za-z]|3rd Party Mac Developer[^"]*:[[:space:]]*[A-Za-z]' \
        "$SCRIPTS_DIR" 2>/dev/null >> "$HARD_SIGN"
    # Team-id-shaped token anywhere else under scripts/ is at least suspicious.
    grep -rIn --binary-files=without-match -E '[A-Z0-9]{10}' "$SCRIPTS_DIR" 2>/dev/null \
        | team_shaped >> "$SOFT_SIGN"
fi
sort -u "$HARD_SIGN" -o "$HARD_SIGN"
sort -u "$SOFT_SIGN" -o "$SOFT_SIGN"
comm -13 "$HARD_SIGN" "$SOFT_SIGN" > "$TMP/signing_soft_only.txt"
if [ -s "$HARD_SIGN" ]; then
    fail "a signing identity looks hardcoded under app/macos/scripts (it must come from the environment)"
    show_hits "$HARD_SIGN" 10
else
    pass "no hardcoded signing identity under app/macos/scripts (team is read from the environment)"
fi
if [ -s "$TMP/signing_soft_only.txt" ]; then
    warn "identifier-shaped token under app/macos/scripts; confirm it is not a signing identity"
    show_hits "$TMP/signing_soft_only.txt" 6
fi

# ===========================================================================
head1 "2. Static design conformance"
# ===========================================================================

if [ "$DESIGN_COUNT" -gt 0 ]; then

# --- em dashes -------------------------------------------------------------
HITS="$TMP/emdash_swift.txt"
design_files_args grep -nH -- "$EM_DASH" > "$HITS"
if [ -s "$HITS" ]; then
    fail "em dash found in onboarding Swift ($(grep -c . "$HITS") line(s))"
    show_hits "$HITS" 10
else
    pass "no em dash in onboarding Swift"
fi

HITS="$TMP/emdash_strings.txt"
if [ -f "$BASE_STRINGS" ]; then
    grep -nH -E '^[[:space:]]*"onboarding\.' "$BASE_STRINGS" | grep -- "$EM_DASH" > "$HITS"
    if [ -s "$HITS" ]; then
        fail "em dash found in onboarding.* values in Base.lproj ($(grep -c . "$HITS") line(s))"
        show_hits "$HITS" 10
    else
        pass "no em dash in any onboarding.* value in Base.lproj"
    fi
else
    fail "Base.lproj/Localizable.strings not found at $BASE_STRINGS"
fi

# --- corner radius scale ---------------------------------------------------
HITS="$TMP/radius.txt"
design_files_args grep -nHoE '(cornerRadius:[[:space:]]*[0-9]+(\.[0-9]+)?|\.cornerRadius\([0-9]+(\.[0-9]+)?\))' \
    | grep -vE '[^0-9](6|10|16)(\)|$)' > "$HITS"
if [ -s "$HITS" ]; then
    fail "cornerRadius literal off the 6/10/16 scale (use DesignConstants.CornerRadius)"
    show_hits "$HITS" 10
else
    pass "every cornerRadius literal in onboarding views is on the 6/10/16 scale"
fi

# --- spacing / padding scale (warn only) -----------------------------------
HITS="$TMP/spacing.txt"
design_files_args grep -nHoE '(spacing:[[:space:]]*[0-9]+(\.[0-9]+)?|\.padding\([0-9]+(\.[0-9]+)?\)|\.padding\(\.[a-z]+,[[:space:]]*[0-9]+(\.[0-9]+)?\))' \
    | grep -vE '[^0-9](0|4|8|10|12|20|24)(\)|$)' > "$HITS"
if [ -s "$HITS" ]; then
    warn "spacing/padding literal off the 4/8/10/12/20/24 scale ($(grep -c . "$HITS") occurrence(s), not a hard fail)"
    show_hits "$HITS" 10
else
    pass "every spacing/padding literal in onboarding views is on the 4/8/10/12/20/24 scale"
fi

# --- window frame 760 x 580 ------------------------------------------------
# Either literal, or a .frame(width:height:) fed by two named constants that are
# themselves declared as 760 and 580 somewhere in the onboarding views.
HITS="$TMP/frame.txt"
design_files_args grep -nHE '\.frame\([^)]*width:[[:space:]]*760[^)]*height:[[:space:]]*580' > "$HITS"
NAMED_FRAME="$TMP/frame_named.txt"
design_files_args grep -nHE '\.frame\([[:space:]]*width:[[:space:]]*[A-Za-z_][A-Za-z0-9_.]*[Ww]idth[[:space:]]*,[[:space:]]*height:[[:space:]]*[A-Za-z_][A-Za-z0-9_.]*[Hh]eight[[:space:]]*\)' > "$NAMED_FRAME"
NAMED_760=$(design_files_args grep -cE 'let[[:space:]]+[A-Za-z_][A-Za-z0-9_]*[Ww]idth[^=]*=[[:space:]]*760\b' | awk -F: '{s+=$NF} END{print s+0}')
NAMED_580=$(design_files_args grep -cE 'let[[:space:]]+[A-Za-z_][A-Za-z0-9_]*[Hh]eight[^=]*=[[:space:]]*580\b' | awk -F: '{s+=$NF} END{print s+0}')
if [ -s "$HITS" ]; then
    pass "root frame is 760 x 580"
    show_hits "$HITS" 3
elif [ -s "$NAMED_FRAME" ] && [ "${NAMED_760:-0}" -gt 0 ] && [ "${NAMED_580:-0}" -gt 0 ]; then
    pass "root frame is 760 x 580 via named constants"
    show_hits "$NAMED_FRAME" 3
else
    fail "no .frame(width: 760, height: 580) found in the onboarding views"
fi

# --- "System Default" is the first device option ---------------------------
if [ -n "$MIC_FILE" ]; then
    # Only count System Default where it is RENDERED as an option. Declarations
    # (private var usingSystemDefault / systemDefaultBinding / comments) do not
    # prove ordering, so they are filtered out before comparing line numbers.
    SD_LINE=$(grep -nEi 'systemDefault|system\.default|System Default' "$MIC_FILE" \
        | grep -vE ':[[:space:]]*(//|///|\*)' \
        | grep -vE ':[[:space:]]*(private |fileprivate |public |internal )*(var|func)[[:space:]]' \
        | head -1 | cut -d: -f1)
    DEV_LINE=$(grep -nE 'ForEach\([^)]*[Dd]evices' "$MIC_FILE" | head -1 | cut -d: -f1)
    PREPEND=$(grep -nEi '(insert\([^)]*at:[[:space:]]*0|\[[^]]*systemDefault[^]]*\][[:space:]]*\+|systemDefault\][[:space:]]*\+)' "$MIC_FILE" | head -1)
    if [ -z "$SD_LINE" ]; then
        fail "no rendered System Default device option found in ${MIC_FILE#$REPO_ROOT/} (declarations alone do not count)"
    elif [ -n "$PREPEND" ]; then
        pass "System Default is prepended to the device list (${MIC_FILE#$REPO_ROOT/}:${PREPEND%%:*})"
    elif [ -z "$DEV_LINE" ]; then
        warn "System Default option rendered at ${MIC_FILE#$REPO_ROOT/}:$SD_LINE but no device list found to order it against"
    elif [ "$SD_LINE" -lt "$DEV_LINE" ]; then
        pass "System Default is the first device option (${MIC_FILE#$REPO_ROOT/}:$SD_LINE, before the device list at :$DEV_LINE)"
    else
        fail "System Default is NOT first: rendered at ${MIC_FILE#$REPO_ROOT/}:$SD_LINE, after the device list at :$DEV_LINE"
    fi

    # --- device name on one line -------------------------------------------
    # Both modifiers have to sit on the SAME Text, so pair each
    # .truncationMode(.tail) with its nearest .lineLimit(1) rather than taking
    # the first of each in the file (an unrelated label earlier in the same view
    # would otherwise look like a mismatch).
    LL=""
    TM=""
    BEST=99999
    for tm_line in $(grep -nE '\.truncationMode\(\.tail\)' "$MIC_VIEW_FILE" | cut -d: -f1); do
        for ll_line in $(grep -nE '\.lineLimit\(1\)' "$MIC_VIEW_FILE" | cut -d: -f1); do
            D=$((ll_line - tm_line)); [ "$D" -lt 0 ] && D=$((-D))
            if [ "$D" -lt "$BEST" ]; then
                BEST=$D
                LL="$ll_line"
                TM="$tm_line"
            fi
        done
    done
    if [ -z "$LL" ]; then
        LL=$(grep -nE '\.lineLimit\(1\)' "$MIC_VIEW_FILE" | head -1 | cut -d: -f1)
    fi
    if [ -z "$TM" ]; then
        TM=$(grep -nE '\.truncationMode\(\.tail\)' "$MIC_VIEW_FILE" | head -1 | cut -d: -f1)
    fi
    if [ -n "$LL" ] && [ -n "$TM" ]; then
        DELTA=$((LL - TM)); [ "$DELTA" -lt 0 ] && DELTA=$((-DELTA))
        if [ "$DELTA" -le 5 ]; then
            pass "device name is single line: .lineLimit(1) at ${MIC_VIEW_FILE#$REPO_ROOT/}:$LL and .truncationMode(.tail) at :$TM"
        else
            warn ".lineLimit(1) (:$LL) and .truncationMode(.tail) (:$TM) are far apart in ${MIC_VIEW_FILE#$REPO_ROOT/}; confirm both apply to the device name"
            PASSES=$((PASSES + 1))
        fi
    else
        fail "device name is missing $( [ -z "$LL" ] && printf '.lineLimit(1) ' )$( [ -z "$TM" ] && printf '.truncationMode(.tail) ' )in ${MIC_VIEW_FILE#$REPO_ROOT/}"
    fi

    # --- Sound Settings is a bordered secondary button ----------------------
    SS_LINE=$(grep -nEi 'Sound Settings|onboarding\.mic\.soundSettings|onboarding\.audio\.open\.sound|soundSettings' "$MIC_VIEW_FILE" | head -1 | cut -d: -f1)
    if [ -z "$SS_LINE" ]; then
        fail "no Sound Settings button found in ${MIC_VIEW_FILE#$REPO_ROOT/}"
    else
        LO=$((SS_LINE - 8)); [ "$LO" -lt 1 ] && LO=1
        HI=$((SS_LINE + 12))
        if sed -n "${LO},${HI}p" "$MIC_VIEW_FILE" | grep -qE '\.buttonStyle\(\.bordered\)'; then
            pass "Sound Settings uses .buttonStyle(.bordered) (${MIC_VIEW_FILE#$REPO_ROOT/}:$SS_LINE)"
        else
            fail "Sound Settings at ${MIC_VIEW_FILE#$REPO_ROOT/}:$SS_LINE does not use .buttonStyle(.bordered) within +/-10 lines"
            detail "$(sed -n "${LO},${HI}p" "$MIC_VIEW_FILE" | grep -nE '\.buttonStyle\([^)]*\)' | head -3)"
        fi
    fi
else
    fail "could not locate the microphone step view (no onboarding file references availableDevices / onboarding.mic.*)"
fi

# --- no hardcoded color literals -------------------------------------------
HITS="$TMP/colors.txt"
design_files_args grep -nHE '(#colorLiteral|Color\((red|hue|white|hex|displayP3Red)[:.]|Color\(\.sRGB|Color\(\.displayP3|NSColor\((red|calibratedRed|deviceRed|white)[:.]|UIColor\(red:)' > "$HITS"
if [ -s "$HITS" ]; then
    fail "hardcoded color literal in onboarding views (semantic colors and materials only, must be correct in light and dark)"
    show_hits "$HITS" 10
else
    pass "no hardcoded color literals in onboarding views"
fi

# --- all 8 steps present and rendered --------------------------------------
STEP_ENUM_FILE=$(all_onb_args grep -lE 'enum[[:space:]]+[A-Za-z]*Step' | head -1)
STEPS="welcome permissions source configure setup microphone tryIt done"
MISSING=""
for s in $STEPS; do
    case "$s" in
        permissions) pat='permissions|permission' ;;
        microphone)  pat='microphone|\bmic\b' ;;
        tryIt)       pat='tryIt|testRecording|tryStep' ;;
        done)        pat='\bdone\b|completion|complete' ;;
        *)           pat="$s" ;;
    esac
    if ! all_onb_args grep -qEi "$pat"; then MISSING="$MISSING $s"; fi
done
if [ -n "$MISSING" ]; then
    fail "onboarding step(s) not found in any onboarding source file:$MISSING"
else
    RENDERED_OK=1
    if [ -n "$STEP_ENUM_FILE" ]; then
        ENUM_START=$(grep -nE 'enum[[:space:]]+[A-Za-z]*Step' "$STEP_ENUM_FILE" | head -1 | cut -d: -f1)
        CASES=$(awk -v s="$ENUM_START" 'NR>=s{ if (NR>s && /^[[:space:]]*}/) exit; if (/^[[:space:]]*case[[:space:]]/) c++ } END{print c+0}' "$STEP_ENUM_FILE")
        if [ "$CASES" -ne 8 ]; then
            fail "step enum in ${STEP_ENUM_FILE#$REPO_ROOT/}:$ENUM_START declares $CASES cases, expected 8"
            RENDERED_OK=0
        fi
        for s in $STEPS; do
            all_onb_args grep -qE "case[[:space:]]+\.?$s\b" || {
                warn "step '$s' has no matching 'case .$s' switch arm; confirm it is actually rendered"
            }
        done
    else
        if ! all_onb_args grep -qE 'totalSteps[[:space:]]*(=|:)[[:space:]]*8|\.allCases\.count'; then
            warn "no *Step enum and no totalSteps = 8 marker found; step count is unverified"
        fi
    fi
    [ "$RENDERED_OK" -eq 1 ] && pass "all 8 steps present and rendered (welcome, permissions, source, configure, setup, microphone, tryIt, done)"
fi

fi # DESIGN_COUNT > 0

# ===========================================================================
head1 "3. Localization"
# ===========================================================================

if [ "$ALL_ONB_COUNT" -gt 0 ] && [ -f "$BASE_STRINGS" ]; then

# --- unlocalized user visible Text("literal") ------------------------------
# Allowed: Text(localized: "k"), Text("k".localized), Text(verbatim:), Text(var).
# Flagged: any Text("...") whose literal contains real words once interpolation
# segments are removed.
HITS="$TMP/unlocalized.txt"
all_onb_args awk '
    {
        line = $0
        idx = 0
        while (match(substr(line, idx + 1), /Text\([[:space:]]*"/)) {
            start = idx + RSTART + RLENGTH
            idx = start
            rest = substr(line, start)
            q = index(rest, "\"")
            if (q == 0) break
            lit = substr(rest, 1, q - 1)
            after = substr(rest, q + 1, 12)
            if (after ~ /^\.localized/) continue
            probe = lit
            gsub(/\\\([^)]*\)/, "", probe)
            if (probe ~ /[A-Za-z][A-Za-z]/) {
                printf "%s:%d: Text(\"%s\")\n", FILENAME, FNR, lit
            }
        }
    }' > "$HITS"
if [ -s "$HITS" ]; then
    fail "unlocalized Text(\"literal\") in onboarding views ($(grep -c . "$HITS") occurrence(s))"
    show_hits "$HITS" 12
else
    pass "no unlocalized Text(\"literal\") in onboarding views"
fi

HITS="$TMP/unlocalized_other.txt"
# Dotted, space-free literals are localization keys or SF Symbol names, not copy.
all_onb_args grep -nHE '(Label|Button|Toggle|TextField|SecureField)\([[:space:]]*"[^"]*[A-Za-z][A-Za-z]' \
    | grep -vE '"\.localized|localized:' \
    | grep -vE '\([[:space:]]*"[A-Za-z0-9]+(\.[A-Za-z0-9]+)+"' > "$HITS"
if [ -s "$HITS" ]; then
    warn "possible unlocalized literal in a non-Text control ($(grep -c . "$HITS") occurrence(s), review manually)"
    show_hits "$HITS" 8
fi

# --- every onboarding.* key referenced in Swift exists in Base.lproj -------
USED="$TMP/used_keys.txt"
DYNAMIC="$TMP/dynamic_keys.txt"
find "$APP_SRC" -name '*.swift' -type f -print0 \
    | xargs -0 grep -ohE '"onboarding\.[A-Za-z0-9._]*' 2>/dev/null \
    | sed 's/^"//' | sort -u > "$TMP/used_raw.txt"
grep -E '\.$' "$TMP/used_raw.txt" > "$DYNAMIC" 2>/dev/null
grep -vE '\.$' "$TMP/used_raw.txt" > "$USED" 2>/dev/null

DEFINED="$TMP/defined_keys.txt"
sed -nE 's/^[[:space:]]*"(onboarding\.[^"]+)"[[:space:]]*=.*/\1/p' "$BASE_STRINGS" | sort -u > "$DEFINED"

MISSING_KEYS="$TMP/missing_keys.txt"
comm -23 "$USED" "$DEFINED" > "$MISSING_KEYS"
if [ -s "$MISSING_KEYS" ]; then
    fail "$(grep -c . "$MISSING_KEYS") onboarding key(s) used in Swift but missing from Base.lproj"
    while IFS= read -r k; do
        [ -z "$k" ] && continue
        LOC=$(find "$APP_SRC" -name '*.swift' -type f -print0 | xargs -0 grep -nH -m1 -F "\"$k\"" 2>/dev/null | head -1)
        detail "${LOC:-$k}"
    done < "$MISSING_KEYS"
else
    pass "every onboarding.* key referenced in Swift exists in Base.lproj ($(grep -c . "$USED") key(s))"
fi

if [ -s "$DYNAMIC" ]; then
    warn "$(grep -c . "$DYNAMIC") dynamically built onboarding key prefix(es); existence cannot be proven statically"
    show_hits "$DYNAMIC" 6
fi

UNUSED=$(comm -13 "$USED" "$DEFINED" | grep -c . || true)
info "Base.lproj defines $(grep -c . "$DEFINED") onboarding.* keys; $(grep -c . "$USED") referenced, $UNUSED unreferenced (dead keys are informational)"

# --- locale coverage, information only -------------------------------------
BASE_N=$(grep -c . "$DEFINED")
LAGGING=""
for d in "$LOC_DIR"/*.lproj; do
    name=$(basename "$d" .lproj)
    [ "$name" = "Base" ] && continue
    f="$d/Localizable.strings"
    [ -f "$f" ] || { LAGGING="$LAGGING $name:none"; continue; }
    n=$(grep -cE '^[[:space:]]*"onboarding\.' "$f" 2>/dev/null || echo 0)
    [ "$n" -ne "$BASE_N" ] && LAGGING="$LAGGING $name:$n"
done
if [ -n "$LAGGING" ]; then
    warn "locales behind Base ($BASE_N onboarding keys):$LAGGING"
    info "run the localisation-syncer to translate them; the Base fallback below is what keeps this from shipping raw identifiers"
else
    info "all $(ls -d "$LOC_DIR"/*.lproj | wc -l | tr -d ' ') locales match Base at $BASE_N onboarding keys"
fi

# --- the untranslated-key safety net ---------------------------------------
# Translation drift is expected and owned elsewhere, so it is a warning above.
# What is NOT acceptable is a missing key rendering as its raw identifier, so
# the shared lookup helper MUST resolve through the Base table first.
HELPER="$REPO_ROOT/app/macos/hyperwhisper/Extensions/LocalizedString.swift"
if [ ! -f "$HELPER" ]; then
    fail "shared localization helper not found at ${HELPER#$REPO_ROOT/}"
elif grep -qE 'Base' "$HELPER" && grep -qE 'localizedValueIfPresent|fallingBackTo' "$HELPER"; then
    pass "missing keys fall back to the Base value, not the raw identifier (${HELPER#$REPO_ROOT/})"
else
    fail "no Base fallback in ${HELPER#$REPO_ROOT/}: an untranslated key will render as its identifier in all $(ls -d "$LOC_DIR"/*.lproj | wc -l | tr -d ' ') locales"
fi

else
    fail "localization checks skipped: no onboarding Swift files or no Base.lproj/Localizable.strings"
fi

# ===========================================================================
head1 "4. Regression guards for the four known bugs"
# ===========================================================================

if [ "$ALL_ONB_COUNT" -gt 0 ]; then

# Build a function index over every onboarding source file.
FUNCS="$TMP/funcs.txt"
: > "$FUNCS"
while IFS= read -r f; do
    [ -z "$f" ] && continue
    extract_funcs "$f" | while IFS='|' read -r name start end; do
        echo "$f|$name|$start|$end"
    done >> "$FUNCS"
done < "$ALL_ONB"

func_body() { sed -n "$3,$4p" "$1"; }

# --- Bug 1: deferral must not commit the staged source configuration -------
# A "commit" is the real primitive: writing the default Mode through Core Data
# or re-pointing the active mode. Reachability is computed over the onboarding
# call graph, but never propagated *through* a completion-named function
# (completion is allowed to commit; deferral and plain forward nav are not).
COMMIT_PRIMITIVE='createOrUpdateMode\(|selectMode\(|findDefaultMode\(|applySelectedSourceToDefaultMode\('
COMPLETION_NAME='complete|finish|confirmDone'
ROLLBACK_NAME='rollback|restore|revert|undo'
DEFERRAL_NAME='skip|setUpLater|setupLater|later|defer|dismiss|cancelOnboarding|onboardingCancel'
FORWARD_NAME='navigateForward|goForward|advance|nextStep|continueTapped|forward'

# Completion-named and rollback-named functions are ALLOWED to write the default
# Mode (completion is the commit boundary; rollback restores the prior mode), so
# they are never treated as illegal committers and never propagate to callers.
COMMITTERS="$TMP/committers.txt"; : > "$COMMITTERS"
COMPLETERS="$TMP/completers.txt"; : > "$COMPLETERS"
ROLLBACKERS="$TMP/rollbackers.txt"; : > "$ROLLBACKERS"
while IFS='|' read -r f name start end; do
    [ -z "$f" ] && continue
    if func_body "$f" "$name" "$start" "$end" | grep -qE "$COMMIT_PRIMITIVE"; then
        if echo "$name" | grep -qiE "$ROLLBACK_NAME"; then
            echo "$name" >> "$ROLLBACKERS"
        elif echo "$name" | grep -qiE "$COMPLETION_NAME"; then
            echo "$name" >> "$COMPLETERS"
        else
            echo "$name|$f|$start" >> "$COMMITTERS"
        fi
    fi
done < "$FUNCS"

# Transitive closure (3 levels), skipping allowed-name intermediaries.
for _ in 1 2 3; do
    while IFS='|' read -r f name start end; do
        [ -z "$f" ] && continue
        grep -q "^$name|" "$COMMITTERS" && continue
        echo "$name" | grep -qiE "$COMPLETION_NAME|$ROLLBACK_NAME" && continue
        body="$(func_body "$f" "$name" "$start" "$end")"
        while IFS='|' read -r cname _cf _cs; do
            [ -z "$cname" ] && continue
            [ "$cname" = "$name" ] && continue
            if printf '%s' "$body" | grep -qE "(^|[^A-Za-z0-9_.])$cname[[:space:]]*\("; then
                echo "$name|$f|$start" >> "$COMMITTERS"
                break
            fi
        done < "$COMMITTERS"
    done < "$FUNCS"
    sort -u "$COMMITTERS" -o "$COMMITTERS"
done

BUG1="$TMP/bug1.txt"; : > "$BUG1"
while IFS='|' read -r f name start end; do
    [ -z "$f" ] && continue
    if echo "$name" | grep -qiE "$DEFERRAL_NAME"; then
        if grep -q "^$name|" "$COMMITTERS"; then
            echo "${f#$REPO_ROOT/}:$start: deferral func $name() reaches a default-Mode write" >> "$BUG1"
            continue
        fi
        body="$(func_body "$f" "$name" "$start" "$end")"
        while IFS= read -r cname; do
            [ -z "$cname" ] && continue
            if printf '%s' "$body" | grep -qE "(^|[^A-Za-z0-9_.])$cname[[:space:]]*\("; then
                echo "${f#$REPO_ROOT/}:$start: deferral func $name() calls committing func $cname()" >> "$BUG1"
                break
            fi
        done < "$COMPLETERS"
    elif echo "$name" | grep -qiE "$FORWARD_NAME"; then
        if grep -q "^$name|" "$COMMITTERS"; then
            echo "${f#$REPO_ROOT/}:$start: forward-nav func $name() commits the staged source before completion" >> "$BUG1"
        fi
    fi
done < "$FUNCS"

if [ -s "$BUG1" ]; then
    fail "BUG 1 regression: Set Up Later / forward navigation can mutate production state"
    show_hits "$BUG1" 8
    detail "stage the source configuration and commit it only at explicit completion, or roll back on deferral"
else
    pass "BUG 1 guard: no deferral or forward-nav path reaches a default-Mode write"
    [ -s "$COMPLETERS" ] && detail "commit boundary: $(tr '\n' ' ' < "$COMPLETERS")"
    [ -s "$ROLLBACKERS" ] && detail "rollback path: $(tr '\n' ' ' < "$ROLLBACKERS") (exempt by name, review once)"
    if ! all_onb_args grep -qiE 'rollback|restoreSnapshot|revertStaged|stagedSource|pendingSource|PendingSourceCommit'; then
        warn "no staged-commit or rollback vocabulary found (stagedSource / pendingSource / rollback); confirm the commit boundary exists"
    fi
fi

# --- Bug 2: Parakeet download errors must be surfaced ----------------------
# `$errorMessage` (the Combine projected value) counts too: publishing the
# manager's error into the flow is exactly how it reaches the setup screen.
WHISPER_ERR=$(all_onb_args grep -nHE 'whisper[A-Za-z]*Manager\.\$?errorMessage|whisper[A-Za-z]*\.\$?errorMessage' | head -3)
PARAKEET_ERR=$(all_onb_args grep -nHE 'parakeet[A-Za-z]*Manager\.\$?errorMessage|parakeet[A-Za-z]*\.\$?errorMessage|parakeetError' | head -3)
if [ -n "$PARAKEET_ERR" ]; then
    pass "BUG 2 guard: the setup error surface reads ParakeetModelManager's errorMessage"
    detail "$(printf '%s' "$PARAKEET_ERR" | head -1)"
elif [ -n "$WHISPER_ERR" ]; then
    fail "BUG 2 regression: only WhisperModelManager.errorMessage is rendered; Parakeet download failures stay invisible"
    detail "$(printf '%s' "$WHISPER_ERR" | head -1)"
else
    fail "BUG 2 regression: no model-download error is surfaced at all in the onboarding setup step"
fi

# --- Bug 3: cloud activation must not outlive the sheet -------------------
ACT="$TMP/activate.txt"
all_onb_args grep -nHE 'activateLicense[[:space:]]*\(' > "$ACT"
if [ ! -s "$ACT" ]; then
    warn "BUG 3 guard: no activateLicense( call site found in the onboarding views; confirm the Cloud branch still activates"
else
    BUG3="$TMP/bug3.txt"; : > "$BUG3"
    while IFS= read -r hit; do
        [ -z "$hit" ] && continue
        f="${hit%%:*}"; rest="${hit#*:}"; ln="${rest%%:*}"
        # Enclosing function body, or a +/- window if we cannot resolve one.
        fstart=""; fend=""
        while IFS='|' read -r ff fname fs fe; do
            [ "$ff" = "$f" ] || continue
            if [ "$ln" -ge "$fs" ] && [ "$ln" -le "$fe" ]; then fstart="$fs"; fend="$fe"; fi
        done < "$FUNCS"
        if [ -z "$fstart" ]; then
            fstart=$((ln - 10)); [ "$fstart" -lt 1 ] && fstart=1
            fend=$((ln + 14))
        fi
        body="$(sed -n "${fstart},${fend}p" "$f")"
        lo=$((ln - 4)); [ "$lo" -lt 1 ] && lo=1
        near="$(sed -n "${lo},${ln}p" "$f")"
        BARE_TASK=0
        printf '%s' "$near" | grep -qE '(^|[^=])[[:space:]]*Task[[:space:]]*(\.detached)?[[:space:]]*\{' \
            && ! printf '%s' "$near" | grep -qE '=[[:space:]]*Task' && BARE_TASK=1
        GUARDED=0
        # Only the enclosing function counts: a tracked Task somewhere else in the
        # onboarding code must not excuse an untracked one here.
        printf '%s' "$body" | grep -qE 'Task\.isCancelled|isCancelled|activationTask|\.cancel\(\)|guard[^\n]*(isActive|isPresented|!isDismissed|generation|token)' && GUARDED=1
        if [ "$BARE_TASK" -eq 1 ] && [ "$GUARDED" -eq 0 ]; then
            echo "${f#$REPO_ROOT/}:$ln: bare unstructured Task around activateLicense with no cancellation or dismissal guard" >> "$BUG3"
        fi
    done < "$ACT"
    if [ -s "$BUG3" ]; then
        fail "BUG 3 regression: cloud activation can complete after the user leaves onboarding"
        show_hits "$BUG3" 6
        detail "hold the Task on the view model, cancel it on dismissal, and guard the post-await commit"
    else
        pass "BUG 3 guard: every activateLicense call site is tracked, cancellable or dismissal guarded"
    fi
fi

# Paid moat: no client side entitlement bypass may be introduced here.
HITS="$TMP/bypass.txt"
all_onb_args grep -nHiE 'bypassLicense|skipLicenseCheck|fakeLicense|testLicenseKey|debugEntitlement|forceEntitled|HYPERWHISPER_DEBUG_LICENSE' > "$HITS"
if [ -s "$HITS" ]; then
    fail "possible client-side entitlement bypass in onboarding (Cloud entitlement is enforced server side)"
    show_hits "$HITS" 6
else
    pass "no client-side entitlement bypass in the onboarding views"
fi

fi # ALL_ONB_COUNT > 0

# --- Bug 4: real onboarding coverage in hyperwhisperTests -----------------
ONB_TESTS="$TMP/onb_tests.txt"
: > "$ONB_TESTS"
if [ -d "$TESTS_DIR" ]; then
    # Prefer files whose NAME is about onboarding; only fall back to files that
    # merely mention it, so an unrelated suite cannot satisfy this gate quietly.
    find "$TESTS_DIR" -name '*Onboarding*.swift' -type f > "$ONB_TESTS"
    if [ ! -s "$ONB_TESTS" ]; then
        grep -rlE 'Onboarding|onboarding' "$TESTS_DIR" --include='*.swift' 2>/dev/null > "$ONB_TESTS"
        [ -s "$ONB_TESTS" ] && warn "no hyperwhisperTests file is named *Onboarding*Tests.swift; falling back to files that merely mention onboarding"
    fi
fi
if [ ! -s "$ONB_TESTS" ]; then
    fail "BUG 4 regression: hyperwhisperTests contains no onboarding tests at all"
else
    TEST_FN_COUNT=$(tr '\n' '\0' < "$ONB_TESTS" | xargs -0 grep -hcE '(func[[:space:]]+test[A-Za-z0-9_]*\(|@Test)' 2>/dev/null | awk '{s+=$1} END{print s+0}')
    MISSING_TOPICS=""
    check_topic() {
        if ! tr '\n' '\0' < "$ONB_TESTS" | xargs -0 grep -qiE "$2" 2>/dev/null; then
            MISSING_TOPICS="$MISSING_TOPICS $1"
        fi
    }
    check_topic "step-gating"        'canContinue|gating|stepGat|advanc|cannotContinue'
    check_topic "permission-recovery" 'permission'
    check_topic "source-cloud"        'cloud'
    check_topic "source-onDevice"     'onDevice|on_device|parakeet|whisper'
    check_topic "source-apiKey"       'apiKey|api_key|provider|byok'
    check_topic "download-failure"    'downloadFail|failedDownload|downloadError|errorMessage|failure'
    check_topic "microphone"          'microphone|inputLevel|mic'
    check_topic "setUpLater-rollback" 'rollback|setUpLater|setupLater|skip|defer'
    check_topic "completion"          'complet|finish|\bdone\b'
    N_MISSING=$(echo "$MISSING_TOPICS" | wc -w | tr -d ' ')
    info "onboarding test files: $(tr '\n' ' ' < "$ONB_TESTS" | sed "s#$REPO_ROOT/##g")"
    if [ "$TEST_FN_COUNT" -lt 8 ]; then
        fail "BUG 4 regression: only $TEST_FN_COUNT onboarding test function(s) in hyperwhisperTests (need real coverage, at least 8)"
    elif [ "$N_MISSING" -ge 3 ]; then
        fail "BUG 4 regression: $TEST_FN_COUNT onboarding tests but $N_MISSING required topic(s) uncovered:$MISSING_TOPICS"
    else
        pass "BUG 4 guard: $TEST_FN_COUNT onboarding test function(s) in hyperwhisperTests"
        [ -n "$MISSING_TOPICS" ] && warn "onboarding topic(s) not obviously covered (name matching is heuristic):$MISSING_TOPICS"
    fi
fi

# ===========================================================================
head1 "5. Build and unit tests"
# ===========================================================================

note "hyperwhisperUITests are NOT run by this script. The XCUITest runner is"
note "rejected by macOS container consent on this machine before any test code"
note "executes, so a UI test result here would be meaningless. Nothing below"
note "claims UI test coverage."

if [ "$RUN_BUILD" -eq 0 ]; then
    BLOCKED=1
    warn "build skipped (--static-only): this run cannot certify the app"
else
    info "building $SCHEME (Debug, derived data: $DERIVED_DATA)"
    xcodebuild \
        -project "$PROJECT" \
        -scheme "$SCHEME" \
        -derivedDataPath "$DERIVED_DATA" \
        -configuration Debug \
        DEVELOPMENT_TEAM="${DEVELOPMENT_TEAM:-}" \
        ENABLE_DEBUG_DYLIB=NO \
        CODE_SIGNING_ALLOWED=NO \
        build > "$BUILD_LOG" 2>&1
    BUILD_RC=$?
    if [ "$BUILD_RC" -ne 0 ]; then
        fail "build FAILED (xcodebuild exit $BUILD_RC)"
        grep -nE 'error:|fatal error|\*\* BUILD FAILED' "$BUILD_LOG" | head -30 | while IFS= read -r l; do detail "$l"; done
        detail "full log: $BUILD_LOG (copied to /tmp/verify-onboarding-build.log)"
        cp "$BUILD_LOG" /tmp/verify-onboarding-build.log 2>/dev/null
    else
        WARN_N=$(grep -cE ' warning: ' "$BUILD_LOG" 2>/dev/null | head -1)
        pass "build succeeded ($WARN_N compiler warning line(s))"
    fi
fi

if [ "$RUN_TESTS" -eq 0 ]; then
    BLOCKED=1
    warn "unit tests skipped by flag: this run cannot certify the app"
elif [ "${RUN_BUILD:-1}" -eq 1 ] && [ "${BUILD_RC:-1}" -ne 0 ]; then
    BLOCKED=1
    warn "unit tests not run because the build failed"
elif [ -z "${DEVELOPMENT_TEAM:-}" ]; then
    BLOCKED=2
    fail "unit tests CANNOT BE SIGNED: DEVELOPMENT_TEAM is empty in the environment"
    detail "xcodebuild cannot host a test bundle without real signing, so the suite did NOT run."
    detail "Nothing here says the tests pass. Re-run as:"
    detail "  DEVELOPMENT_TEAM=<your team id> $SCRIPT_PATH"
    detail "Never commit the team id: this repo intentionally ships an empty DEVELOPMENT_TEAM."
else
    info "running hyperwhisperTests only (real signing, team from the environment)"
    rm -rf "$RESULT_BUNDLE"
    xcodebuild \
        -project "$PROJECT" \
        -scheme "$SCHEME" \
        -derivedDataPath "$DERIVED_DATA" \
        -configuration Debug \
        -resultBundlePath "$RESULT_BUNDLE" \
        -only-testing:hyperwhisperTests \
        DEVELOPMENT_TEAM="${DEVELOPMENT_TEAM:-}" \
        ENABLE_DEBUG_DYLIB=NO \
        test > "$TEST_LOG" 2>&1
    TEST_RC=$?

    T_PASS=""; T_FAIL=""; T_SKIP=""
    if [ -d "$RESULT_BUNDLE" ]; then
        SUMMARY="$TMP/summary.json"
        xcrun xcresulttool get test-results summary --path "$RESULT_BUNDLE" > "$SUMMARY" 2>/dev/null
        if [ -s "$SUMMARY" ]; then
            T_PASS=$(sed -nE 's/.*"passedTests"[[:space:]]*:[[:space:]]*([0-9]+).*/\1/p' "$SUMMARY" | tail -1)
            T_FAIL=$(sed -nE 's/.*"failedTests"[[:space:]]*:[[:space:]]*([0-9]+).*/\1/p' "$SUMMARY" | tail -1)
            T_SKIP=$(sed -nE 's/.*"skippedTests"[[:space:]]*:[[:space:]]*([0-9]+).*/\1/p' "$SUMMARY" | tail -1)
        fi
    fi
    if [ -z "$T_PASS" ]; then
        T_PASS=$(grep -oE "Test case '[^']+' passed" "$TEST_LOG" | sort -u | wc -l | tr -d ' ')
        T_FAIL=$(grep -oE "Test case '[^']+' failed" "$TEST_LOG" | sort -u | wc -l | tr -d ' ')
        T_SKIP="?"
    fi

    if [ "$TEST_RC" -eq 0 ] && grep -q '\*\* TEST SUCCEEDED \*\*' "$TEST_LOG"; then
        pass "hyperwhisperTests: $T_PASS passed, ${T_FAIL:-0} failed, ${T_SKIP:-0} skipped"
    else
        fail "hyperwhisperTests FAILED: ${T_PASS:-?} passed, ${T_FAIL:-?} failed, ${T_SKIP:-?} skipped (xcodebuild exit $TEST_RC)"
        grep -E "Test case '[^']+' failed|error:|XCTAssert|Issue recorded|\*\* TEST FAILED \*\*|user declined consent" "$TEST_LOG" \
            | head -30 | while IFS= read -r l; do detail "$(printf '%s' "$l" | cut -c1-220)"; done
        cp "$TEST_LOG" /tmp/verify-onboarding-test.log 2>/dev/null
        detail "full log copied to /tmp/verify-onboarding-test.log"
    fi
fi

# ===========================================================================
head1 "Verdict"
# ===========================================================================

if [ "$HARD_FAILS" -gt 0 ]; then
    printf '%s\n' "$C_RED${C_BLD}FAIL${C_OFF}: $HARD_FAILS hard check(s) failed, $WARNS warning(s), $PASSES passed. UI tests not run (container consent)."
    info "failed checks:"
    while IFS= read -r l; do detail "- $l"; done < "$FAIL_LINES"
    exit 1
fi
if [ "$BLOCKED" -eq 2 ]; then
    printf '%s\n' "$C_RED${C_BLD}BLOCKED${C_OFF}: static checks passed but the unit suite could not be signed, so nothing is certified."
    exit 2
fi
if [ "$BLOCKED" -ne 0 ]; then
    printf '%s\n' "$C_YEL${C_BLD}INCOMPLETE${C_OFF}: static checks passed but the build/tests were skipped, so this is NOT a pass."
    exit 2
fi
printf '%s\n' "$C_GRN${C_BLD}PASS${C_OFF}: build green, hyperwhisperTests green, $PASSES checks passed, $WARNS warning(s). UI tests not run (container consent)."
exit 0
