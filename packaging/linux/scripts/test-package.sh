#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PACKAGE_PATH="${1:-}"
PUBLISH_DIR="${HYPERWHISPER_PUBLISH_DIR:-/tmp/hyperwhisper-linux-publish}"
EXPECTED_VERSION="${HYPERWHISPER_PACKAGE_VERSION:-1.0.0}"

if [[ -z "$PACKAGE_PATH" ]]; then
    PACKAGE_PATH="$("$SCRIPT_DIR"/build-deb.sh --publish-dir "$PUBLISH_DIR" --output-dir /tmp)"
fi
if [[ ! -f "$PACKAGE_PATH" ]]; then
    echo "Package not found: $PACKAGE_PATH" >&2
    exit 2
fi

TEST_ROOT="$(mktemp -d -t hyperwhisper-package-test.XXXXXXXX)"
cleanup() {
    rm -rf -- "$TEST_ROOT"
}
trap cleanup EXIT

ROOTFS="$TEST_ROOT/rootfs"
CONTROL="$TEST_ROOT/control"
install -d -m 0755 "$ROOTFS" "$CONTROL"
dpkg-deb --extract "$PACKAGE_PATH" "$ROOTFS"
dpkg-deb --control "$PACKAGE_PATH" "$CONTROL"

assert_file() {
    if [[ ! -f "$1" ]]; then
        echo "Expected file: $1" >&2
        exit 1
    fi
}

assert_file "$ROOTFS/usr/lib/hyperwhisper/HyperWhisper"
assert_file "$ROOTFS/usr/lib/hyperwhisper/libhyperwhisper_core.so"
assert_file "$ROOTFS/usr/lib/hyperwhisper/parakeet-engine/parakeet-engine"
assert_file "$ROOTFS/usr/lib/hyperwhisper/parakeet-engine/libsherpa-onnx-c-api.so"
assert_file "$ROOTFS/usr/share/applications/hyperwhisper.desktop"
assert_file "$ROOTFS/usr/share/pixmaps/hyperwhisper.png"
assert_file "$ROOTFS/usr/share/hyperwhisper/packaging/70-hyperwhisper-input.rules"
assert_file "$ROOTFS/usr/share/hyperwhisper/companions/status-notifier.py"
assert_file "$ROOTFS/usr/share/hyperwhisper/companions/gnome/42/extension.js"
assert_file "$ROOTFS/usr/share/hyperwhisper/companions/gnome/45/extension.js"
assert_file "$ROOTFS/usr/share/hyperwhisper/companions/kde/package/contents/code/main.js"
assert_file "$ROOTFS/usr/share/hyperwhisper/companions/kde/kde-active-window.py"
assert_file "$ROOTFS/usr/share/man/man1/hyperwhisper.1.gz"
assert_file "$ROOTFS/usr/share/man/man1/hyperwhisper-companionctl.1.gz"
test -x "$ROOTFS/usr/lib/hyperwhisper/HyperWhisper"
test -x "$ROOTFS/usr/lib/hyperwhisper/parakeet-engine/parakeet-engine"
test "$(readlink "$ROOTFS/usr/bin/hyperwhisper")" = "../lib/hyperwhisper/HyperWhisper"
test "$(readlink "$ROOTFS/usr/bin/hyperwhisper-companionctl")" = "../share/hyperwhisper/companions/hyperwhisper-companionctl"
test -x "$ROOTFS/usr/share/hyperwhisper/companions/status-notifier.py"
test -x "$ROOTFS/usr/share/hyperwhisper/companions/hyperwhisper-companionctl"
test "$(dpkg-deb --field "$PACKAGE_PATH" Architecture)" = "amd64"
test "$(dpkg-deb --field "$PACKAGE_PATH" Version)" = "$EXPECTED_VERSION"

dependencies="$(dpkg-deb --field "$PACKAGE_PATH" Depends)"
for dependency in \
    libc6 libpulse0 pulseaudio-utils libx11-6 libatspi2.0-0 gir1.2-atspi-2.0 libglib2.0-bin python3 python3-gi \
    tesseract-ocr udev wl-clipboard wmctrl x11-utils xclip xdg-desktop-portal; do
    grep -qw "$dependency" <<< "$dependencies"
done

rule_template="$ROOTFS/usr/share/hyperwhisper/packaging/70-hyperwhisper-input.rules"
grep -q 'ENV{ID_INPUT_KEYBOARD}=="1"' "$rule_template"
grep -q 'GROUP="hyperwhisper-input"' "$rule_template"
grep -q 'MODE="0640"' "$rule_template"
grep -q 'KERNEL=="uinput"' "$rule_template"
grep -q 'MODE="0660"' "$rule_template"
active_rules="$(sed -e '/^[[:space:]]*#/d' -e '/^[[:space:]]*$/d' "$rule_template")"
if grep -Eq 'MODE="0?[0-7]{2}[1-7]"|TAG\+="uaccess"' <<< "$active_rules"; then
    echo "udev rule grants access beyond the dedicated input group." >&2
    exit 1
fi

home_reference="\$HOME"
if grep -Eq '(/home/|/root/|~[/ ])' "$CONTROL/postinst" "$CONTROL/postrm" \
    || grep -Fq "$home_reference" "$CONTROL/postinst" "$CONTROL/postrm"; then
    echo "Maintainer scripts must not write to user homes." >&2
    exit 1
fi

export HYPERWHISPER_MAINTAINER_ROOT="$ROOTFS"
"$CONTROL/postinst" configure
installed_rule="$ROOTFS/etc/udev/rules.d/70-hyperwhisper-input.rules"
assert_file "$installed_rule"
first_checksum="$(sha256sum "$installed_rule" | awk '{print $1}')"
"$CONTROL/postinst" configure
test "$first_checksum" = "$(sha256sum "$installed_rule" | awk '{print $1}')"
test "$(grep -c '^hyperwhisper-input:' "$ROOTFS/etc/group")" = "1"
test ! -d "$ROOTFS/home"

"$CONTROL/postrm" upgrade 1.0.1
assert_file "$installed_rule"
"$CONTROL/postrm" remove
test ! -e "$installed_rule"
"$CONTROL/postrm" purge

if find "$ROOTFS" -type f -perm /0002 -print -quit | grep -q .; then
    echo "Package or maintainer script created a world-writable file." >&2
    exit 1
fi

echo "PASS package structure, permissions, dependencies, and maintainer scripts"
