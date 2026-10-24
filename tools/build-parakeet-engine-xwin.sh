#!/bin/bash
# Cross-build the x64 parakeet-engine.exe on Linux with clang-cl + the MSVC CRT and Windows SDK
# from xwin, for a box without Visual Studio. tools/build-parakeet-engine.bat stays the reference
# build; this one links the same sherpa-onnx-c-api.dll (an import library generated from the
# shipped DLL's export table) with the same dynamic CRT (/MD).
#
#   tools/build-parakeet-engine-xwin.sh            # writes Resources/parakeet-engine/x64/parakeet-engine.exe
#
# Needs: clang-cl and lld-link >= 19 (older clang rejects the current MSVC STL), llvm-dlltool,
# llvm-readobj, and an xwin splat (`xwin --accept-license --arch x86_64 splat --output $XWIN`).
# XWIN defaults to ~/xbuild/xwin; LLVM_BIN to /usr/lib/llvm-19/bin.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
XWIN=${XWIN:-$HOME/xbuild/xwin}
LLVM_BIN=${LLVM_BIN:-/usr/lib/llvm-19/bin}
SHERPA_TAG=v1.13.3   # the tag tools/build-sherpa-onnx.bat pins
RES="$ROOT/app/windows/HyperWhisper/Resources/parakeet-engine/x64"
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

mkdir -p "$WORK/inc/sherpa-onnx/c-api"
curl -sfL "https://raw.githubusercontent.com/k2-fsa/sherpa-onnx/$SHERPA_TAG/sherpa-onnx/c-api/c-api.h" \
  -o "$WORK/inc/sherpa-onnx/c-api/c-api.h"

{ echo "LIBRARY sherpa-onnx-c-api.dll"; echo "EXPORTS"
  "$LLVM_BIN/llvm-readobj" --coff-exports "$RES/sherpa-onnx-c-api.dll" | awk '/Name: /{print "  "$2}'; } > "$WORK/sherpa.def"
"$LLVM_BIN/llvm-dlltool" -m i386:x86-64 -d "$WORK/sherpa.def" -l "$WORK/sherpa-onnx-c-api.lib"

"$LLVM_BIN/clang-cl" --target=x86_64-pc-windows-msvc -fuse-ld=lld-link /std:c++17 /O2 /MD /EHsc /W3 \
  /DNDEBUG /D_CRT_SECURE_NO_WARNINGS \
  /imsvc "$XWIN/crt/include" /imsvc "$XWIN/sdk/include/ucrt" /imsvc "$XWIN/sdk/include/um" /imsvc "$XWIN/sdk/include/shared" \
  /I "$WORK/inc" "$ROOT/tools/parakeet-engine/main.cpp" /Fo"$WORK/main.obj" /Fe"$RES/parakeet-engine.exe" \
  /link /LIBPATH:"$XWIN/crt/lib/x86_64" /LIBPATH:"$XWIN/sdk/lib/um/x86_64" /LIBPATH:"$XWIN/sdk/lib/ucrt/x86_64" \
  "$WORK/sherpa-onnx-c-api.lib" /SUBSYSTEM:CONSOLE
echo "built $RES/parakeet-engine.exe"
