#!/bin/bash
set -euo pipefail

# Remove any existing code signatures from the bundled whisper.xcframework
# to let Xcode sign fresh during Archive/Export.

FW_BASE="$(cd "$(dirname "$0")" && pwd)/whisper.xcframework/macos-arm64_x86_64/whisper.framework"

if [ ! -d "$FW_BASE" ]; then
  echo "whisper.framework not found at: $FW_BASE" >&2
  exit 1
fi

echo "Stripping existing code signatures from whisper.framework..."

targets=(
  "$FW_BASE/whisper"
  "$FW_BASE/libggml.dylib"
  "$FW_BASE/libggml-base.dylib"
  "$FW_BASE/libggml-blas.dylib"
  "$FW_BASE/libggml-cpu.dylib"
  "$FW_BASE/libggml-metal.dylib"
)

for t in "${targets[@]}"; do
  if [ -e "$t" ]; then
    echo "- removing signature: $t"
    codesign --remove-signature "$t" || true
  fi
done

echo "- removing framework bundle signature: $FW_BASE"
codesign --remove-signature "$FW_BASE" || true

echo "Done. Clean + Archive so Xcode signs everything with Developer ID."

