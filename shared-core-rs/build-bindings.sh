#!/usr/bin/env bash
# Generate UniFFI bindings for all platforms from the built library.
# Swift + Kotlin use the self-hosted (version-matched) generator; C# uses the
# external uniffi-bindgen-cs (skipped with a warning if not installed). See README.
set -euo pipefail
cd "$(dirname "$0")"

echo "==> building release lib (for library-mode metadata)"
cargo build --release
LIB="target/release/libhyperwhisper_core.dylib"

echo "==> swift bindings"
cargo run --features cli --bin uniffi-bindgen -- generate \
  --library "$LIB" --language swift --out-dir bindings/swift

echo "==> kotlin bindings"
cargo run --features cli --bin uniffi-bindgen -- generate \
  --library "$LIB" --language kotlin --out-dir bindings/kotlin

if command -v uniffi-bindgen-cs >/dev/null 2>&1; then
  echo "==> c# bindings"
  uniffi-bindgen-cs --library "$LIB" --out-dir bindings/csharp
else
  echo "!! uniffi-bindgen-cs not installed — skipping C#. Install with:"
  echo "   cargo install uniffi-bindgen-cs --git https://github.com/NordSecurity/uniffi-bindgen-cs --tag v0.9.2+v0.28.3"
fi

# The macOS app vendors byte-copies of the Swift binding (project.pbxproj
# references those paths directly). Refresh them here, or CI's binding-drift job
# fails on the next PR. The C# binding has no copies — every .NET head compiles
# bindings/csharp/ through HyperWhisper.SharedCore (issue #275).
echo "==> syncing vendored copies"
../tools/check-binding-drift.sh --fix

echo "==> done"
