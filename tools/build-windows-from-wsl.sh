#!/usr/bin/env bash
# Build the HyperWhisper Windows app from WSL by driving the *Windows* .NET SDK
# through WSL interop. The repo lives on the Linux filesystem; Windows dotnet.exe
# reaches it via the \\wsl.localhost\ UNC path (wslpath -w translates it).
#
# This builds the C# app only. The C++ parakeet-engine daemon binaries are already
# committed under app/windows/HyperWhisper/Resources/parakeet-engine/x64, so the app
# builds without rebuilding C++. To rebuild the daemon (e.g. after the Qwen3 changes
# in tools/parakeet-engine/main.cpp), see "Rebuilding the daemon" below — that needs
# the sherpa-onnx source + VS BuildTools and is a separate, heavier step.
#
# Usage:  tools/build-windows-from-wsl.sh [Debug|Release]
set -euo pipefail

CONFIG="${1:-Debug}"
DOTNET="/mnt/c/Program Files/dotnet/dotnet.exe"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CSPROJ="$REPO_ROOT/app/windows/HyperWhisper/HyperWhisper.csproj"

if [[ ! -x "$DOTNET" ]]; then
  echo "ERROR: Windows dotnet.exe not found at: $DOTNET" >&2
  echo "Install the .NET 10 SDK on Windows (not the Linux dotnet — WPF needs Windows)." >&2
  exit 1
fi

CSPROJ_WIN="$(wslpath -w "$CSPROJ")"
echo "Building ($CONFIG): $CSPROJ_WIN"
"$DOTNET" build "$CSPROJ_WIN" -c "$CONFIG" -v m

# ---------------------------------------------------------------------------
# Rebuilding the daemon (C++) from WSL — heavier, needs the sherpa-onnx source:
#
#   1. Clone sherpa-onnx at the pinned tag somewhere on the *Windows* drive
#      (UNC/9P compiles are very slow), e.g. C:\src\sherpa-onnx, and check out
#      v1.13.3 (see tools/build-sherpa-onnx.bat — it pins this).
#   2. Point SHERPA_DIR in tools/build-sherpa-onnx.bat at that clone, then run it
#      from WSL via cmd.exe:
#        cmd.exe /c "$(wslpath -w tools/build-sherpa-onnx.bat)"
#   3. Rebuild the daemon against the same tag:
#        cmd.exe /c "$(wslpath -w tools/build-parakeet-engine.bat)"
#      which also copies the fresh parakeet-engine.exe + DLLs into Resources.
#
# The header (sherpa-onnx/c-api/c-api.h) and the DLL MUST come from the same tag
# (the qwen3_asr struct ABI footgun) — always rebuild the daemon after re-checking
# out sherpa-onnx.
# ---------------------------------------------------------------------------
