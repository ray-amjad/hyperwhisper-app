# shared-core-rs

The HyperWhisper **shared Rust core**: one Rust codebase that owns the
platform-agnostic business logic and is consumed by macOS (Swift), Windows (C#),
iOS (Swift) and — on the roadmap — Android (Kotlin) through **UniFFI**.

Each app keeps its native edges (audio capture, local model inference, text
injection, history/stats stores, UI). Rust owns the middle.

## Layout

```
shared-core-rs/
  Cargo.toml              workspace
  rust-toolchain.toml     pinned toolchain (1.86.0)
  crates/
    hw-core/              UniFFI umbrella — the single FFI surface (all #[uniffi::export]s)
    hw-phonetic/          Beider-Morse phonetic encoding (was app/macos/rphonetic-ffi)
    hw-text/              pure text logic + M1b prompt builder (build_system_prompt/info)
    hw-net/               sans-I/O contract + 12 cloud STT providers + retry/health/errors
    hw-license/           license validate + trial limits + 24h cache / 7-day grace (KeyValueStore)
    hw-backup/            backup validate + universal-v2 bidirectional settings/mode mapping
    hw-catalog/           models + cloud-stt + cloud-pp + app-type catalog lookups
  bindings/               generated Swift / C# / Kotlin (committed)
  tests/fixtures/         golden input -> output fixtures captured from current impls
```

All leaf crates are re-exported through `hw-core`, the single `#[uniffi::export]`
surface. Persistence is injected via a `KeyValueStore` UniFFI foreign trait the
platform implements (macOS: UserDefaults; Windows: Credential Manager + JSON);
time is injected (`now_unix_secs`) — the core has no clock or RNG.

## Artifacts

The `hw-core` crate's `[lib] name = "hyperwhisper_core"` drives the binary names:

| Platform | Artifact | crate-type |
|----------|----------|------------|
| macOS / iOS | `libhyperwhisper_core.a` (universal / xcframework) | `staticlib` |
| Windows | `hyperwhisper_core.dll` | `cdylib` |
| Android | `libhyperwhisper_core.so` | `cdylib` |

The UniFFI namespace is `hyperwhisper_core`, so generators emit
`hyperwhisper_core.swift`, `hyperwhisper_core.cs`, and Kotlin package
`hyperwhisper_core`.

## Version pinning (important)

`uniffi` is pinned to **=0.28.3** in `[workspace.dependencies]`. This is dictated
by the community C# generator **`uniffi-bindgen-cs`**, whose latest tag
(`v0.9.2+v0.28.3`) targets uniffi 0.28.3. **Bump both together**, never one
alone. Swift/Kotlin use the self-hosted `uniffi-bindgen` (always version-matched
because it's built from this workspace).

## Build & rebuild

### Rust check / test

```bash
cd shared-core-rs
cargo test --release          # unit + golden parity tests
cargo clippy --all-targets
```

### macOS universal static lib

```bash
cd shared-core-rs
cargo build --release --target aarch64-apple-darwin
cargo build --release --target x86_64-apple-darwin
lipo -create \
  target/aarch64-apple-darwin/release/libhyperwhisper_core.a \
  target/x86_64-apple-darwin/release/libhyperwhisper_core.a \
  -output ../app/macos/hyperwhisper/Libraries/libhyperwhisper_core.a
```

Helper: `./build-apple.sh` does the two builds + `lipo` and copies the result.

### Windows DLL (both architectures)

Cross-compiling the MSVC DLL from macOS is not supported; build on Windows or in
CI. The Windows app's call sites (Wave 3) are committed but compile only against
the generated C# binding here — a real `dotnet build` needs the DLL present:

```bash
cargo build --release --target x86_64-pc-windows-msvc
cargo build --release --target aarch64-pc-windows-msvc
# Copy each hyperwhisper_core.dll to the app's per-arch resource dirs:
#   target/x86_64-pc-windows-msvc/release/hyperwhisper_core.dll
#     -> app/windows/HyperWhisper/Resources/rust-core/x64/hyperwhisper_core.dll
#   target/aarch64-pc-windows-msvc/release/hyperwhisper_core.dll
#     -> app/windows/HyperWhisper/Resources/rust-core/arm64/hyperwhisper_core.dll
```

The csproj `<Content>` block (Exists-guarded) copies the DLL to the output root.
After the DLL is in place, run `dotnet build -c Debug` from
`app/windows/HyperWhisper/` to verify the C# call-site swaps.

CI does this automatically: `.github/workflows/windows-release.yml` builds both
MSVC targets and copies the DLLs into `Resources/rust-core/{x64,arm64}/` before
`dotnet`. A `<Target Name="EnsureRustCoreDll">` in the csproj now hard-errors at
build/publish time if the DLL for the active RID is missing (it used to be
silently omitted → `DllNotFoundException` at startup).

### Generate bindings

Swift + Kotlin (self-hosted generator, library mode — needs a built dylib):

```bash
cargo build --release
cargo run --features cli --bin uniffi-bindgen -- generate \
  --library target/release/libhyperwhisper_core.dylib \
  --language swift --out-dir bindings/swift

cargo run --features cli --bin uniffi-bindgen -- generate \
  --library target/release/libhyperwhisper_core.dylib \
  --language kotlin --out-dir bindings/kotlin
```

C# (external tool, version-locked — install once):

```bash
cargo install uniffi-bindgen-cs \
  --git https://github.com/NordSecurity/uniffi-bindgen-cs --tag v0.9.2+v0.28.3
uniffi-bindgen-cs --library target/release/libhyperwhisper_core.dylib \
  --out-dir bindings/csharp
```

Helper: `./build-bindings.sh` generates Swift + Kotlin (+ C# if the tool is
installed).

## Post-review behavior notes (PR #896)

Cross-cutting policy changes from the code-review + Codex review round — they
affect **every** provider/platform through the shared core, so they are called out
here and covered by golden tests:

- **Transcription retries: `MAX_ATTEMPTS` 4 → 8** (`hw-net/retry.rs`). Restores the
  pre-unification macOS resilience without going back to 10. Worst-case backoff
  with no `Retry-After` is the exponential series `1+2+4+8+16+32+64s` ≈ 127s across
  the 7 retryable attempts; an honored `Retry-After` is still clamped to 10s/sleep.
- **ElevenLabs health probe** (`hw-net/health.rs`) now POSTs to
  `/v1/speech-to-text` (empty body) instead of `GET /v1/models`: a 400/422 ("body
  rejected") grades **healthy** (the key can reach STT), 401/403 unauthorized. A
  models-list-only key no longer shows green.
- **Remote-override TTL is server-driven** (`hw-license/cache.rs`): the platform
  writes the config response's `Cache-Control: max-age` into KV key
  `com.hyperwhisper.config.maxAgeSecs`; the core honors it, defaulting to **6h**
  when absent and clamping to a **24h** upper bound. Both platform-write halves
  (macOS `ConfigService`/`LicenseUsageTracker`, Windows `ConfigService`) and the
  core-read half ship together — missing either silently changes the freshness
  window.
- **HW Cloud / routed vocabulary** is again capped at 100 terms with
  case-insensitive de-dup (`normalize_vocabulary_capped`); the Beider-Morse encoder
  config is cached in a `OnceLock`; Deepgram restores the plain-text-2xx transcript
  fallback; the spoken "new line" command no longer fires mid-word ("newlines").

### Round 2 (max-effort review + unaddressed Codex P2s)

- **Phonetic split drops empty segments** (`hw-phonetic`): a `code1||code2`-style
  encoding no longer yields a stray empty code that spuriously matches words.
- **Lenient `strip_wrapper_markers` rejects prompt leaks** (`hw-text`): an unwrapped
  post-process response containing any known prompt section tag
  (`<APPLICATION_CONTEXT>`, `<SCREEN_CONTEXT>`, …) returns empty so callers keep the
  raw transcript instead of pasting the leaked system prompt / OCR text.
- **Soniox vocab is sanitized** (`hw-net`): each `context` term runs through
  `sanitize_vocabulary_word` (drop `</>`, collapse whitespace), restoring the macOS
  behavior. Scoped to Soniox — other providers' verified vocab behavior is unchanged.
- **License clock-rollback guard** (`hw-license/cache.rs`): a backward clock
  (negative delta) is treated as stale in `should_revalidate` /
  `cached_status_within_grace` / `remote_override_if_fresh`, so it can't pin a stale
  cache/override as perpetually fresh.
- **Lossless backup passthrough** (`hw-backup`): the `Universal*` structs gained a
  `#[serde(flatten)] extra` map so unknown top-level / category / mode / vocabulary
  keys survive a round-trip; the macOS settings extension is now **category-keyed**
  (`platformExtensions.macos.settings.{audio,general,storage,advanced,shortcuts,aiModel}`)
  and import routes each key home by its recorded category — no per-key allowlist
  drift. Foreign per-mode `platformExtensions` slices already pass through verbatim;
  the platforms persist them (mac→v2→Windows→v2→mac retains the `windows` slice).

Retry **jitter** (0–30%) is added platform-side at each sleep point (macOS/Windows
`RustRetry`) since the core is RNG-free, restoring the lost thundering-herd defense.

**Documented limitation (intentionally unchanged):** `hw-backup` post-processing
catalog validation is *not* validated against `hw-catalog`. `hw-backup` is sans-I/O
with a frozen alias snapshot by design; depending on `hw-catalog` would violate
that principle. Left as a known limitation.

## Distribution

- **Milestones 0–4 (now):** build locally, commit the prebuilt binaries + pilot
  bindings (the same hybrid pattern `rphonetic-ffi` used).
- **Milestone 5+:** a GitHub Actions matrix cross-compiles every target; binaries
  leave git history and are pulled at app-build time.
