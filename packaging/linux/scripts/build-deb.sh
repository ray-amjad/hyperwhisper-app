#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/../../.." && pwd)"
DEBIAN_SOURCE="$REPO_ROOT/packaging/linux/debian"
PUBLISH_DIR=""
OUTPUT_DIR="$REPO_ROOT/artifacts/linux"
ICON_PATH="$REPO_ROOT/icon-200px-round.png"
VERSION="1.0.0"
ARCHITECTURE="amd64"

usage() {
    echo "Usage: $0 --publish-dir DIR [--output-dir DIR] [--icon FILE] [--version 1.0.0]" >&2
}

while (($# > 0)); do
    case "$1" in
        --publish-dir)
            PUBLISH_DIR="${2:-}"
            shift 2
            ;;
        --output-dir)
            OUTPUT_DIR="${2:-}"
            shift 2
            ;;
        --icon)
            ICON_PATH="${2:-}"
            shift 2
            ;;
        --version)
            VERSION="${2:-}"
            shift 2
            ;;
        --architecture)
            ARCHITECTURE="${2:-}"
            shift 2
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            usage
            echo "Unknown argument: $1" >&2
            exit 2
            ;;
    esac
done

if [[ -z "$PUBLISH_DIR" || ! -d "$PUBLISH_DIR" ]]; then
    usage
    echo "--publish-dir must name an existing self-contained publish directory." >&2
    exit 2
fi
if [[ ! -x "$PUBLISH_DIR/HyperWhisper" ]]; then
    echo "Publish directory is missing executable HyperWhisper apphost." >&2
    exit 2
fi
if [[ ! -f "$PUBLISH_DIR/libhyperwhisper_core.so" ]]; then
    echo "Publish directory is missing libhyperwhisper_core.so." >&2
    exit 2
fi
if [[ ! -f "$ICON_PATH" ]]; then
    echo "Icon does not exist: $ICON_PATH" >&2
    exit 2
fi
if [[ ! "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+([+~.-][0-9A-Za-z.+~-]+)?$ ]]; then
    echo "Invalid Debian package version: $VERSION" >&2
    exit 2
fi
if [[ "$ARCHITECTURE" != "amd64" ]]; then
    echo "Linux v1 packaging supports amd64 only." >&2
    exit 2
fi
for tool in patchelf strip; do
    if ! command -v "$tool" >/dev/null 2>&1; then
        echo "$tool is required to normalize packaged native libraries." >&2
        exit 2
    fi
done

BUILD_ROOT="$(mktemp -d -t hyperwhisper-deb.XXXXXXXX)"
cleanup() {
    rm -rf -- "$BUILD_ROOT"
}
trap cleanup EXIT

PACKAGE_ROOT="$BUILD_ROOT/hyperwhisper"
install -d -m 0755 \
    "$PACKAGE_ROOT/DEBIAN" \
    "$PACKAGE_ROOT/usr/bin" \
    "$PACKAGE_ROOT/usr/lib/hyperwhisper" \
    "$PACKAGE_ROOT/usr/share/applications" \
    "$PACKAGE_ROOT/usr/share/lintian/overrides" \
    "$PACKAGE_ROOT/usr/share/man/man1" \
    "$PACKAGE_ROOT/usr/share/pixmaps" \
    "$PACKAGE_ROOT/usr/share/hyperwhisper/packaging" \
    "$PACKAGE_ROOT/usr/share/hyperwhisper/companions" \
    "$PACKAGE_ROOT/usr/share/doc/hyperwhisper"

cp -a -- "$PUBLISH_DIR/." "$PACKAGE_ROOT/usr/lib/hyperwhisper/"
# NuGet publish assets include every supported host RID. The Debian artifact is
# amd64-only, so retaining foreign binaries is both wasteful and misleading to
# package tooling. Keep the three Linux x64 Whisper backends and the x64 LLama
# variants; discard Windows, macOS, ARM, and foreign accelerator assets.
rm -rf -- \
    "$PACKAGE_ROOT/usr/lib/hyperwhisper/runtimes/linux-arm" \
    "$PACKAGE_ROOT/usr/lib/hyperwhisper/runtimes/linux-arm64" \
    "$PACKAGE_ROOT/usr/lib/hyperwhisper/runtimes/macos-arm64" \
    "$PACKAGE_ROOT/usr/lib/hyperwhisper/runtimes/macos-x64" \
    "$PACKAGE_ROOT/usr/lib/hyperwhisper/runtimes/win-arm64" \
    "$PACKAGE_ROOT/usr/lib/hyperwhisper/runtimes/win-x64" \
    "$PACKAGE_ROOT/usr/lib/hyperwhisper/runtimes/win-x86" \
    "$PACKAGE_ROOT/usr/lib/hyperwhisper/runtimes/cuda12/win-x64" \
    "$PACKAGE_ROOT/usr/lib/hyperwhisper/runtimes/vulkan/win-x64"

# Upstream Whisper binaries embed their CI build directories as RUNPATHs.
# Runtime siblings are resolved through the loader and application directory,
# so remove those non-portable paths. Strip only the native objects that ship
# debug symbols in the upstream NuGet payload; do not rewrite managed files.
mapfile -d '' PACKAGED_RUNTIME_LIBRARIES < <(
    find \
        "$PACKAGE_ROOT/usr/lib/hyperwhisper/runtimes/linux-x64" \
        "$PACKAGE_ROOT/usr/lib/hyperwhisper/runtimes/cuda12/linux-x64" \
        "$PACKAGE_ROOT/usr/lib/hyperwhisper/runtimes/vulkan/linux-x64" \
        -type f -name '*.so' -print0
)
for library in "${PACKAGED_RUNTIME_LIBRARIES[@]}"; do
    patchelf --remove-rpath "$library"
    strip --strip-unneeded "$library"
done
strip --strip-unneeded "$PACKAGE_ROOT/usr/lib/hyperwhisper/libe_sqlite3.so"

find "$PACKAGE_ROOT/usr/lib/hyperwhisper" -type d -exec chmod 0755 {} +
find "$PACKAGE_ROOT/usr/lib/hyperwhisper" -type f -exec chmod 0644 {} +
find "$PACKAGE_ROOT/usr/lib/hyperwhisper" -type f -name '*.pdb' -delete
chmod 0755 "$PACKAGE_ROOT/usr/lib/hyperwhisper/HyperWhisper"
if [[ -f "$PACKAGE_ROOT/usr/lib/hyperwhisper/parakeet-engine/parakeet-engine" ]]; then
    chmod 0755 "$PACKAGE_ROOT/usr/lib/hyperwhisper/parakeet-engine/parakeet-engine"
fi
if [[ -f "$PACKAGE_ROOT/usr/lib/hyperwhisper/createdump" ]]; then
    chmod 0755 "$PACKAGE_ROOT/usr/lib/hyperwhisper/createdump"
fi

ln -s ../lib/hyperwhisper/HyperWhisper "$PACKAGE_ROOT/usr/bin/hyperwhisper"
install -m 0644 "$DEBIAN_SOURCE/hyperwhisper.desktop" \
    "$PACKAGE_ROOT/usr/share/applications/hyperwhisper.desktop"
install -m 0644 "$ICON_PATH" "$PACKAGE_ROOT/usr/share/pixmaps/hyperwhisper.png"
install -m 0644 "$DEBIAN_SOURCE/70-hyperwhisper-input.rules" \
    "$PACKAGE_ROOT/usr/share/hyperwhisper/packaging/70-hyperwhisper-input.rules"
cp -a -- "$REPO_ROOT/app/linux/desktop-companions/." "$PACKAGE_ROOT/usr/share/hyperwhisper/companions/"
find "$PACKAGE_ROOT/usr/share/hyperwhisper/companions" -type d -exec chmod 0755 {} +
find "$PACKAGE_ROOT/usr/share/hyperwhisper/companions" -type f -exec chmod 0644 {} +
chmod 0755 "$PACKAGE_ROOT/usr/share/hyperwhisper/companions/status-notifier.py" \
    "$PACKAGE_ROOT/usr/share/hyperwhisper/companions/hyperwhisper-companionctl" \
    "$PACKAGE_ROOT/usr/share/hyperwhisper/companions/kde/kde-active-window.py"
ln -s ../share/hyperwhisper/companions/hyperwhisper-companionctl "$PACKAGE_ROOT/usr/bin/hyperwhisper-companionctl"
install -m 0644 "$DEBIAN_SOURCE/copyright" \
    "$PACKAGE_ROOT/usr/share/doc/hyperwhisper/copyright"
install -m 0644 "$DEBIAN_SOURCE/lintian-overrides" \
    "$PACKAGE_ROOT/usr/share/lintian/overrides/hyperwhisper"
gzip -n -9 -c "$DEBIAN_SOURCE/changelog" \
    > "$PACKAGE_ROOT/usr/share/doc/hyperwhisper/changelog.gz"
chmod 0644 "$PACKAGE_ROOT/usr/share/doc/hyperwhisper/changelog.gz"
gzip -n -9 -c "$DEBIAN_SOURCE/hyperwhisper.1" \
    > "$PACKAGE_ROOT/usr/share/man/man1/hyperwhisper.1.gz"
chmod 0644 "$PACKAGE_ROOT/usr/share/man/man1/hyperwhisper.1.gz"
gzip -n -9 -c "$DEBIAN_SOURCE/hyperwhisper-companionctl.1" \
    > "$PACKAGE_ROOT/usr/share/man/man1/hyperwhisper-companionctl.1.gz"
chmod 0644 "$PACKAGE_ROOT/usr/share/man/man1/hyperwhisper-companionctl.1.gz"

install -m 0755 "$DEBIAN_SOURCE/postinst" "$PACKAGE_ROOT/DEBIAN/postinst"
install -m 0755 "$DEBIAN_SOURCE/postrm" "$PACKAGE_ROOT/DEBIAN/postrm"

INSTALLED_SIZE="$(du -sk "$PACKAGE_ROOT" | awk '{print $1}')"
sed \
    -e "s/@VERSION@/$VERSION/g" \
    -e "s/@ARCHITECTURE@/$ARCHITECTURE/g" \
    -e "s/@INSTALLED_SIZE@/$INSTALLED_SIZE/g" \
    "$DEBIAN_SOURCE/control" > "$PACKAGE_ROOT/DEBIAN/control"
chmod 0644 "$PACKAGE_ROOT/DEBIAN/control"

(
    cd "$PACKAGE_ROOT"
    find usr -type f -print0 | sort -z | xargs -0 md5sum
) > "$PACKAGE_ROOT/DEBIAN/md5sums"
chmod 0644 "$PACKAGE_ROOT/DEBIAN/md5sums"

install -d -m 0755 "$OUTPUT_DIR"
OUTPUT_PATH="$OUTPUT_DIR/hyperwhisper_${VERSION}_${ARCHITECTURE}.deb"
dpkg-deb --root-owner-group --build "$PACKAGE_ROOT" "$OUTPUT_PATH" >&2
printf '%s\n' "$OUTPUT_PATH"
