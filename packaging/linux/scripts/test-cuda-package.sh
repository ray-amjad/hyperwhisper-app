#!/usr/bin/env bash
# Verifies hyperwhisper-cuda_VERSION_amd64.deb (issue #1002): the optional
# CUDA 12 backends, installed at the paths the app reads, pinned to the main
# package version. Pass the main package too, to prove the two never claim the
# same file (dpkg would refuse to install the second one).
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PACKAGE_PATH="${1:-}"
MAIN_PACKAGE_PATH="${2:-}"
EXPECTED_VERSION="${HYPERWHISPER_PACKAGE_VERSION:-1.0.0}"

if [[ -z "$PACKAGE_PATH" || ! -f "$PACKAGE_PATH" ]]; then
    echo "Usage: $0 hyperwhisper-cuda_VERSION_amd64.deb [hyperwhisper_VERSION_amd64.deb]" >&2
    exit 2
fi
if [[ -n "$MAIN_PACKAGE_PATH" && ! -f "$MAIN_PACKAGE_PATH" ]]; then
    echo "Main package not found: $MAIN_PACKAGE_PATH" >&2
    exit 2
fi

TEST_ROOT="$(mktemp -d -t hyperwhisper-cuda-package-test.XXXXXXXX)"
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

test "$(dpkg-deb --field "$PACKAGE_PATH" Package)" = "hyperwhisper-cuda"
test "$(dpkg-deb --field "$PACKAGE_PATH" Architecture)" = "amd64"
test "$(dpkg-deb --field "$PACKAGE_PATH" Version)" = "$EXPECTED_VERSION"
dependencies="$(dpkg-deb --field "$PACKAGE_PATH" Depends)"
# The first relation pins the main package to this exact version, so the two
# packages can never mix releases.
if [[ "${dependencies%%,*}" != "hyperwhisper (= $EXPECTED_VERSION)" ]]; then
    echo "Depends must pin the main package exactly: got '$dependencies'" >&2
    exit 1
fi
# The CUDA files move here from older main packages, so dpkg must be allowed
# to take them over on an upgrade (Debian policy 7.6.1).
for field in Replaces Breaks; do
    relation="$(dpkg-deb --field "$PACKAGE_PATH" "$field")"
    if [[ "$relation" != "hyperwhisper (<< $EXPECTED_VERSION)" ]]; then
        echo "$field must name older main packages: got '$relation'" >&2
        exit 1
    fi
done

# Whisper.net's CUDA 12 backend (LinuxNativeRuntimeLocator and Whisper.net
# both read runtimes/cuda12/linux-x64) and LLamaSharp's CUDA 12 backend
# (runtimes/linux-x64/native/cuda12, read by PackagedLlamaRuntime).
LIB="$ROOTFS/usr/lib/hyperwhisper"
for runtime_library in \
    "$LIB/runtimes/cuda12/linux-x64/libwhisper.so" \
    "$LIB/runtimes/cuda12/linux-x64/libggml-cuda-whisper.so" \
    "$LIB/runtimes/linux-x64/native/cuda12/libllama.so" \
    "$LIB/runtimes/linux-x64/native/cuda12/libggml-cuda.so" \
    "$LIB/runtimes/linux-x64/native/cuda12/libggml-cpu.so"; do
    assert_file "$runtime_library"
    if readelf -d "$runtime_library" | grep -Eq '(RPATH|RUNPATH)'; then
        echo "Packaged runtime retains a build-host library path: $runtime_library" >&2
        exit 1
    fi
    if file "$runtime_library" | grep -q 'not stripped'; then
        echo "Packaged runtime retains debug symbols: $runtime_library" >&2
        exit 1
    fi
done
assert_file "$ROOTFS/usr/share/doc/hyperwhisper-cuda/copyright"
assert_file "$ROOTFS/usr/share/doc/hyperwhisper-cuda/changelog.gz"

# Nothing but the two CUDA trees and the package's doc directory.
unexpected="$(
    cd "$ROOTFS"
    find . -type f \
        ! -path './usr/lib/hyperwhisper/runtimes/cuda12/linux-x64/*' \
        ! -path './usr/lib/hyperwhisper/runtimes/linux-x64/native/cuda12/*' \
        ! -path './usr/share/doc/hyperwhisper-cuda/*' \
        -print
)"
if [[ -n "$unexpected" ]]; then
    echo "Unexpected files in hyperwhisper-cuda:" >&2
    printf '%s\n' "$unexpected" >&2
    exit 1
fi
if [[ -e "$LIB/runtimes/cuda12/win-x64" ]]; then
    echo "Foreign runtime was packaged: cuda12/win-x64" >&2
    exit 1
fi
windows_native_dlls="$(python3 "$SCRIPT_DIR/find-windows-native-dlls.py" "$LIB")"
if [[ -n "$windows_native_dlls" ]]; then
    echo "Windows native DLLs were packaged:" >&2
    printf '%s\n' "$windows_native_dlls" >&2
    exit 1
fi
for script in preinst postinst prerm postrm; do
    if [[ -e "$CONTROL/$script" ]]; then
        echo "hyperwhisper-cuda must not carry maintainer scripts: $script" >&2
        exit 1
    fi
done
if find "$ROOTFS" -type f -perm /0002 -print -quit | grep -q .; then
    echo "Package contains a world-writable file." >&2
    exit 1
fi

if [[ -n "$MAIN_PACKAGE_PATH" ]]; then
    test "$(dpkg-deb --field "$MAIN_PACKAGE_PATH" Package)" = "hyperwhisper"
    test "$(dpkg-deb --field "$MAIN_PACKAGE_PATH" Version)" = "$EXPECTED_VERSION"
    list_files() {
        dpkg-deb --fsys-tarfile "$1" | tar -t | grep -v '/$' | sort
    }
    overlap="$(comm -12 <(list_files "$PACKAGE_PATH") <(list_files "$MAIN_PACKAGE_PATH"))"
    if [[ -n "$overlap" ]]; then
        echo "hyperwhisper-cuda and hyperwhisper both ship:" >&2
        printf '%s\n' "$overlap" >&2
        exit 1
    fi
fi

installed_kib="$(du -sk "$ROOTFS" | awk '{print $1}')"
echo "PASS hyperwhisper-cuda structure, CUDA runtime paths, and Depends: $dependencies (installed ${installed_kib} KiB)"
