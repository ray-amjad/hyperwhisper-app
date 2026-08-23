#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PACKAGE_PATH="${1:-}"
GENERATED_PACKAGE=false
TEST_ROOT="$(mktemp -d -t hyperwhisper-apt-test.XXXXXXXX)"
cleanup() {
    rm -rf -- "$TEST_ROOT"
}
trap cleanup EXIT

if [[ -z "$PACKAGE_PATH" ]]; then
    GENERATED_PACKAGE=true
    PACKAGE_ROOT="$TEST_ROOT/package"
    install -d -m 0755 "$PACKAGE_ROOT/DEBIAN" "$PACKAGE_ROOT/usr/bin"
    cat > "$PACKAGE_ROOT/DEBIAN/control" <<'EOF'
Package: hyperwhisper
Version: 1.0.0
Architecture: amd64
Maintainer: HyperWhisper Contributors <opensource@hyperwhisper.com>
Description: deterministic APT metadata test package
EOF
    printf '#!/bin/sh\nexit 0\n' > "$PACKAGE_ROOT/usr/bin/hyperwhisper"
    chmod 0755 "$PACKAGE_ROOT/usr/bin/hyperwhisper"
    PACKAGE_PATH="$TEST_ROOT/hyperwhisper_1.0.0_amd64.deb"
    dpkg-deb --root-owner-group --build "$PACKAGE_ROOT" "$PACKAGE_PATH" >/dev/null
fi

# A second version proves repository regeneration preserves older packages.
PRIOR_ROOT="$TEST_ROOT/prior-package"
if [[ "$GENERATED_PACKAGE" == true ]]; then
    cp -a -- "$PACKAGE_ROOT" "$PRIOR_ROOT"
    sed -i 's/^Version: .*/Version: 0.9.0/' "$PRIOR_ROOT/DEBIAN/control"
    PRIOR_PACKAGE="$TEST_ROOT/hyperwhisper_0.9.0_amd64.deb"
    dpkg-deb --root-owner-group --build "$PRIOR_ROOT" "$PRIOR_PACKAGE" >/dev/null
else
    PRIOR_PACKAGE="$PACKAGE_PATH"
fi
PACKAGE_ARGS=(--deb "$PACKAGE_PATH")
if [[ "$PRIOR_PACKAGE" != "$PACKAGE_PATH" ]]; then
    PACKAGE_ARGS=(--deb "$PRIOR_PACKAGE" "${PACKAGE_ARGS[@]}")
fi

SOURCE_DATE_EPOCH=0 "$SCRIPT_DIR/generate-apt-repository.sh" \
    "${PACKAGE_ARGS[@]}" \
    --output-dir "$TEST_ROOT/first" >/dev/null
SOURCE_DATE_EPOCH=0 "$SCRIPT_DIR/generate-apt-repository.sh" \
    "${PACKAGE_ARGS[@]}" \
    --output-dir "$TEST_ROOT/second" >/dev/null

diff -ru "$TEST_ROOT/first" "$TEST_ROOT/second"
test -s "$TEST_ROOT/first/dists/stable/main/binary-amd64/Packages"
test -s "$TEST_ROOT/first/dists/stable/main/binary-amd64/Packages.gz"
test -s "$TEST_ROOT/first/dists/stable/Release"
test ! -e "$TEST_ROOT/first/dists/stable/InRelease"
test ! -e "$TEST_ROOT/first/dists/stable/Release.gpg"
grep -Fxq 'Architecture: amd64' \
    "$TEST_ROOT/first/dists/stable/main/binary-amd64/Packages"
if [[ "$PRIOR_PACKAGE" != "$PACKAGE_PATH" ]]; then
    test "$(grep -c '^Package: hyperwhisper$' \
        "$TEST_ROOT/first/dists/stable/main/binary-amd64/Packages")" = 2
    test -s "$TEST_ROOT/first/pool/main/h/hyperwhisper/$(basename "$PRIOR_PACKAGE")"
fi
grep -Fxq 'Date: Thu, 01 Jan 1970 00:00:00 +0000' \
    "$TEST_ROOT/first/dists/stable/Release"
grep -Fq 'main/binary-amd64/Packages.gz' "$TEST_ROOT/first/dists/stable/Release"

if SOURCE_DATE_EPOCH=0 "$SCRIPT_DIR/generate-apt-repository.sh" \
    --deb "$PACKAGE_PATH" --output-dir "$TEST_ROOT/first" >/dev/null 2>&1; then
    echo "Generator unexpectedly accepted a non-empty output directory." >&2
    exit 1
fi

echo "PASS deterministic unsigned APT repository metadata"
