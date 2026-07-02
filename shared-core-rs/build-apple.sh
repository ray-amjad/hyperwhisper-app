#!/usr/bin/env bash
# Build the universal macOS static lib and install it into the macOS app's
# Libraries/ dir (already on LIBRARY_SEARCH_PATHS). See README.md.
set -euo pipefail
cd "$(dirname "$0")"

DEST="../app/macos/hyperwhisper/Libraries/libhyperwhisper_core.a"

# Strip local filesystem paths out of the committed artifact: panic/debug
# metadata otherwise embeds $HOME and the workspace path, and this repo is
# public. (`trim-paths = "all"` in Cargo.toml would replace this once it
# stabilizes on the pinned toolchain; also remap the cargo registry src dir
# that dependency paths resolve under.)
export RUSTFLAGS="${RUSTFLAGS:-} --remap-path-prefix=$PWD=/build --remap-path-prefix=$HOME/.cargo=/cargo --remap-path-prefix=$HOME=~"

echo "==> building aarch64-apple-darwin"
cargo build --release --target aarch64-apple-darwin
echo "==> building x86_64-apple-darwin"
cargo build --release --target x86_64-apple-darwin

echo "==> lipo -> $DEST"
lipo -create \
  target/aarch64-apple-darwin/release/libhyperwhisper_core.a \
  target/x86_64-apple-darwin/release/libhyperwhisper_core.a \
  -output "$DEST"

lipo -info "$DEST"
echo "==> done"
