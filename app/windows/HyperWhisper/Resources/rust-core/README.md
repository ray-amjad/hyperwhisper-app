# rust-core native DLL

This folder holds the prebuilt **HyperWhisper shared Rust core** DLL
(`hyperwhisper_core.dll`) that the generated binding
(`Generated/RustCore/hyperwhisper_core.cs`) P/Invokes.

```
Resources/rust-core/
  x64/hyperwhisper_core.dll      <- win-x64 build (committed through Milestone 4)
  arm64/hyperwhisper_core.dll    <- win-arm64 build
```

The `HyperWhisper.csproj` copies the RID-appropriate DLL to the **output root**
(next to `HyperWhisper.exe`) so the default native-library resolver finds it. The
`Content` items are `Exists()`-guarded, so the project builds fine before the DLL
has been produced (you just won't have phonetic matching until it's present).

## Building the DLL

Cross-compiling the MSVC DLL from macOS is not supported — build on Windows (or
in CI, Milestone 5). From the repo root, in the `shared-core-rs` workspace:

```powershell
rustup target add x86_64-pc-windows-msvc aarch64-pc-windows-msvc
cd shared-core-rs

cargo build --release --target x86_64-pc-windows-msvc
copy target\x86_64-pc-windows-msvc\release\hyperwhisper_core.dll ^
     ..\app\windows\HyperWhisper\Resources\rust-core\x64\

cargo build --release --target aarch64-pc-windows-msvc
copy target\aarch64-pc-windows-msvc\release\hyperwhisper_core.dll ^
     ..\app\windows\HyperWhisper\Resources\rust-core\arm64\
```

## Regenerating the C# binding

The binding only needs regenerating when the Rust FFI surface changes:

```bash
# from shared-core-rs (any platform — the generator runs on macOS/Linux too)
cargo build --release
uniffi-bindgen-cs --library target/release/libhyperwhisper_core.dylib --out-dir bindings/csharp
cp bindings/csharp/hyperwhisper_core.cs ../app/windows/HyperWhisper/Generated/RustCore/
```

The C# generator is version-locked to the uniffi version — see
`shared-core-rs/README.md`.
