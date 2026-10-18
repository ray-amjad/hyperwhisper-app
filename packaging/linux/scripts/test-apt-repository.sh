#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PACKAGE_PATH="${1:-}"
CUDA_PACKAGE_PATH="${2:-}"
GENERATED_PACKAGE=false
if [[ -n "$PACKAGE_PATH" && -z "$CUDA_PACKAGE_PATH" ]]; then
    # The repository must carry both packages, so `apt install hyperwhisper-cuda`
    # works next to `apt install hyperwhisper` (issue #1002).
    echo "Usage: $0 [hyperwhisper_VERSION_amd64.deb hyperwhisper-cuda_VERSION_amd64.deb]" >&2
    exit 2
fi
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
Suggests: hyperwhisper-cuda
Description: deterministic APT metadata test package
EOF
    printf '#!/bin/sh\nexit 0\n' > "$PACKAGE_ROOT/usr/bin/hyperwhisper"
    chmod 0755 "$PACKAGE_ROOT/usr/bin/hyperwhisper"
    PACKAGE_PATH="$TEST_ROOT/hyperwhisper_1.0.0_amd64.deb"
    dpkg-deb --root-owner-group --build "$PACKAGE_ROOT" "$PACKAGE_PATH" >/dev/null

    CUDA_ROOT="$TEST_ROOT/cuda-package"
    install -d -m 0755 "$CUDA_ROOT/DEBIAN" "$CUDA_ROOT/usr/lib/hyperwhisper/runtimes/cuda12/linux-x64"
    cat > "$CUDA_ROOT/DEBIAN/control" <<'EOF'
Package: hyperwhisper-cuda
Version: 1.0.0
Architecture: amd64
Maintainer: HyperWhisper Contributors <opensource@hyperwhisper.com>
Depends: hyperwhisper (= 1.0.0)
Description: deterministic APT metadata test CUDA package
EOF
    printf 'cuda\n' > "$CUDA_ROOT/usr/lib/hyperwhisper/runtimes/cuda12/linux-x64/libwhisper.so"
    CUDA_PACKAGE_PATH="$TEST_ROOT/hyperwhisper-cuda_1.0.0_amd64.deb"
    dpkg-deb --root-owner-group --build "$CUDA_ROOT" "$CUDA_PACKAGE_PATH" >/dev/null
fi
if [[ ! -f "$CUDA_PACKAGE_PATH" ]]; then
    echo "CUDA package not found: $CUDA_PACKAGE_PATH" >&2
    exit 2
fi
VERSION="$(dpkg-deb --field "$PACKAGE_PATH" Version)"
test "$(dpkg-deb --field "$CUDA_PACKAGE_PATH" Package)" = "hyperwhisper-cuda"
test "$(dpkg-deb --field "$CUDA_PACKAGE_PATH" Version)" = "$VERSION"

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
PACKAGE_ARGS=(--deb "$PACKAGE_PATH" --deb "$CUDA_PACKAGE_PATH")
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
PACKAGES_INDEX="$TEST_ROOT/first/dists/stable/main/binary-amd64/Packages"
test -s "$PACKAGES_INDEX"
test -s "$TEST_ROOT/first/dists/stable/main/binary-amd64/Packages.gz"
test -s "$TEST_ROOT/first/dists/stable/Release"
test ! -e "$TEST_ROOT/first/dists/stable/InRelease"
test ! -e "$TEST_ROOT/first/dists/stable/Release.gpg"
grep -Fxq 'Architecture: amd64' "$PACKAGES_INDEX"
if [[ "$PRIOR_PACKAGE" != "$PACKAGE_PATH" ]]; then
    test "$(grep -c '^Package: hyperwhisper$' "$PACKAGES_INDEX")" = 2
    test -s "$TEST_ROOT/first/pool/main/h/hyperwhisper/$(basename "$PRIOR_PACKAGE")"
fi

# Both packages are in the index, and the CUDA stanza is pinned to the main
# package's exact version.
test "$(grep -c '^Package: hyperwhisper-cuda$' "$PACKAGES_INDEX")" = 1
test -s "$TEST_ROOT/first/pool/main/h/hyperwhisper/$(basename "$CUDA_PACKAGE_PATH")"
stanza() {
    awk -v package="$1" -v version="$2" '
        BEGIN { RS = ""; FS = "\n" }
        {
            name = ""; ver = ""
            for (i = 1; i <= NF; i++) {
                if ($i ~ /^Package: /) name = substr($i, 10)
                if ($i ~ /^Version: /) ver = substr($i, 10)
            }
            if (name == package && ver == version) print
        }
    ' "$PACKAGES_INDEX"
}
cuda_stanza="$(stanza hyperwhisper-cuda "$VERSION")"
main_stanza="$(stanza hyperwhisper "$VERSION")"
cuda_depends="$(sed -n 's/^Depends: //p' <<< "$cuda_stanza")"
test "${cuda_depends%%,*}" = "hyperwhisper (= $VERSION)"
grep -Fxq "Filename: pool/main/h/hyperwhisper/$(basename "$CUDA_PACKAGE_PATH")" <<< "$cuda_stanza"
grep -Eq '^Suggests: (.*[ ,])?hyperwhisper-cuda([ ,]|$)' <<< "$main_stanza"

grep -Fxq 'Date: Thu, 01 Jan 1970 00:00:00 +0000' \
    "$TEST_ROOT/first/dists/stable/Release"
grep -Fq 'main/binary-amd64/Packages.gz' "$TEST_ROOT/first/dists/stable/Release"

# apt itself must offer hyperwhisper-cuda from this repository. The apt state
# is isolated, so the host's sources, lists, and dpkg status are never read.
if command -v apt-get >/dev/null 2>&1 && command -v apt-cache >/dev/null 2>&1; then
    APT_ROOT="$TEST_ROOT/apt"
    install -d -m 0755 "$APT_ROOT/lists/partial" "$APT_ROOT/cache/archives/partial" \
        "$APT_ROOT/sources.list.d" "$APT_ROOT/preferences.d" "$APT_ROOT/apt.conf.d"
    : > "$APT_ROOT/status"
    printf 'deb [trusted=yes] file:%s stable main\n' "$TEST_ROOT/first" > "$APT_ROOT/sources.list"
    # APT_CONFIG is read before the host's apt.conf.d, so its Dir::Etc::Parts
    # also keeps host hooks (such as a Docker image's clean hook) out.
    cat > "$APT_ROOT/apt.conf" <<EOF
Dir::Etc::Parts "$APT_ROOT/apt.conf.d";
Dir::Etc::SourceList "$APT_ROOT/sources.list";
Dir::Etc::SourceParts "$APT_ROOT/sources.list.d";
Dir::Etc::PreferencesParts "$APT_ROOT/preferences.d";
Dir::State::Lists "$APT_ROOT/lists";
Dir::State::status "$APT_ROOT/status";
Dir::Cache "$APT_ROOT/cache";
APT::Architecture "amd64";
APT::Architectures { "amd64"; };
Debug::NoLocking "true";
APT::Sandbox::User "$(id -un)";
EOF
    export APT_CONFIG="$APT_ROOT/apt.conf"
    apt-get -qq update >/dev/null
    candidate="$(apt-cache policy hyperwhisper-cuda | awk '$1 == "Candidate:" { print $2 }')"
    if [[ "$candidate" != "$VERSION" ]]; then
        echo "apt does not offer hyperwhisper-cuda $VERSION (candidate: ${candidate:-none})." >&2
        exit 1
    fi
    apt-cache depends "hyperwhisper-cuda=$VERSION" \
        | grep -Eq '^[[:space:]]+Depends: hyperwhisper$'
    echo "apt offers hyperwhisper-cuda $candidate, which depends on hyperwhisper (= $VERSION)"
fi

if SOURCE_DATE_EPOCH=0 "$SCRIPT_DIR/generate-apt-repository.sh" \
    --deb "$PACKAGE_PATH" --output-dir "$TEST_ROOT/first" >/dev/null 2>&1; then
    echo "Generator unexpectedly accepted a non-empty output directory." >&2
    exit 1
fi

echo "PASS deterministic unsigned APT repository metadata with hyperwhisper and hyperwhisper-cuda"
