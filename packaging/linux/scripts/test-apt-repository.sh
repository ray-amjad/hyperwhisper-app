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
    if [[ -d "$TEST_ROOT/keygen-gnupg" ]] && command -v gpgconf >/dev/null 2>&1; then
        gpgconf --homedir "$TEST_ROOT/keygen-gnupg" --kill all >/dev/null 2>&1 || true
    fi
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

# Signing must happen only in a throwaway GnuPG home. Run the generator with a
# fresh HOME, no GNUPGHOME and a private TMPDIR, then prove it wrote nothing to
# ~/.gnupg and left no temporary keyring behind, on success and on failure.
if ! command -v gpg >/dev/null 2>&1 || ! command -v gpgv >/dev/null 2>&1; then
    echo "SKIP signed APT repository check: gpg and gpgv are not installed"
    exit 0
fi
KEYGEN_HOME="$TEST_ROOT/keygen-gnupg"
install -d -m 0700 "$KEYGEN_HOME"
gpg --batch --quiet --homedir "$KEYGEN_HOME" --passphrase '' \
    --quick-generate-key 'HyperWhisper APT test <apt-test@example.invalid>' \
    ed25519 sign never 2>/dev/null
TEST_KEY_FILE="$TEST_ROOT/test-signing-key.asc"
TEST_PUBLIC_KEY_FILE="$TEST_ROOT/test-public-key.asc"
(umask 077 && gpg --batch --quiet --homedir "$KEYGEN_HOME" --armor \
    --export-secret-keys > "$TEST_KEY_FILE")
gpg --batch --quiet --homedir "$KEYGEN_HOME" --armor --export > "$TEST_PUBLIC_KEY_FILE"
gpgconf --homedir "$KEYGEN_HOME" --kill all >/dev/null 2>&1 || true

SIGN_HOME="$TEST_ROOT/sign-home"
SIGN_TMP="$TEST_ROOT/sign-tmp"
install -d -m 0700 "$SIGN_HOME" "$SIGN_TMP"
assert_no_gnupg_leak() {
    if [[ -e "$SIGN_HOME/.gnupg" ]]; then
        echo "APT signing wrote to the default GnuPG home ($1)." >&2
        exit 1
    fi
    if [[ -n "$(find "$SIGN_TMP" -mindepth 1 -maxdepth 1 -print -quit)" ]]; then
        echo "APT signing left a temporary directory behind ($1):" >&2
        ls -la "$SIGN_TMP" >&2
        exit 1
    fi
}
run_isolated_generator() {
    env -u GNUPGHOME HOME="$SIGN_HOME" TMPDIR="$SIGN_TMP" SOURCE_DATE_EPOCH=0 \
        "$SCRIPT_DIR/generate-apt-repository.sh" "$@"
}

run_isolated_generator "${PACKAGE_ARGS[@]}" \
    --output-dir "$TEST_ROOT/signed" --signing-key-file "$TEST_KEY_FILE" >/dev/null
assert_no_gnupg_leak "signed run"
test -s "$TEST_ROOT/signed/dists/stable/InRelease"
test -s "$TEST_ROOT/signed/dists/stable/Release.gpg"
test -s "$TEST_ROOT/signed/hyperwhisper-archive-keyring.asc"
VERIFY_KEYRING="$TEST_ROOT/verify-keyring.gpg"
GNUPGHOME="$KEYGEN_HOME" gpg --batch --yes --dearmor \
    --output "$VERIFY_KEYRING" "$TEST_PUBLIC_KEY_FILE"
gpgv --keyring "$VERIFY_KEYRING" "$TEST_ROOT/signed/dists/stable/InRelease" 2>/dev/null
gpgv --keyring "$VERIFY_KEYRING" "$TEST_ROOT/signed/dists/stable/Release.gpg" \
    "$TEST_ROOT/signed/dists/stable/Release" 2>/dev/null

# A public-only key imports but cannot sign: the generator must fail and the
# EXIT trap must still remove its temporary GnuPG home.
if run_isolated_generator "${PACKAGE_ARGS[@]}" \
    --output-dir "$TEST_ROOT/signed-failure" \
    --signing-key-file "$TEST_PUBLIC_KEY_FILE" >/dev/null 2>&1; then
    echo "Generator unexpectedly signed with a public-only key." >&2
    exit 1
fi
assert_no_gnupg_leak "failed run"
test ! -e "$TEST_ROOT/signed-failure"

echo "PASS signed APT repository metadata; signing left nothing in ~/.gnupg or TMPDIR"
