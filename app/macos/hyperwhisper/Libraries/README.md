# Libraries/

This directory is on `LIBRARY_SEARCH_PATHS` for the `hyperwhisper` target.

| File | Tracked in git? |
|---|---|
| `hyperwhisper_coreFFI.h` | yes — generated UniFFI header, small, reviewable in diffs |
| `libhyperwhisper_core.a` | **no** — built from source |

## Why the static lib isn't committed

It's a 65 MB universal (`arm64` + `x86_64`) release build. Binaries don't
delta-compress, so every rebuild appended another ~65 MB blob to history that
nothing could ever reclaim. By the time it was removed, 11 versions of this one
file were **247 MB — 52% of the entire repository**.

Committing it also made the release wrong in a way that was hard to see.
`macos-release.yml` linked whatever `.a` happened to be checked in, and keeping
it fresh was a manual step. That failed two ways:

- **Loud** — the FFI surface moved, so the archive failed to link with
  `Undefined symbols`. This is what PR #64 hit.
- **Silent** — only a Rust function *body* changed, so the UniFFI checksums
  still matched, the archive linked clean, and the release shipped stale Rust
  with no signal at all.

Both are impossible now: CI and the release workflow each rebuild from source.

## Building it

Either run it yourself once after cloning:

```bash
cd shared-core-rs && ./build-apple.sh
```

…or just build in Xcode and let it tell you. The **RustCore** aggregate target
(which `hyperwhisper` depends on) runs an *Ensure Rust Core* phase that builds
the lib when it's missing, then stops with:

> error: Built the Rust core at … Xcode had already planned this build without
> it, so please build again — it will link this time.

**A fresh clone therefore takes two builds, and that's deliberate.** Xcode
resolves `LIBRARY_SEARCH_PATHS` when it *plans* a build, not when it links: if
the `.a` isn't on disk at planning time, `-lhyperwhisper_core` never reaches
the link command, and the build dies in a wall of `Undefined symbols` that says
nothing about the real cause. Building the lib and asking for one more pass is
the honest version of that. It's why the phase lives in its own target — the
app target never starts, so you get one clear line instead of hundreds of
misleading ones.

The Rust build takes a few minutes; the workspace pins `lto = true` /
`codegen-units = 1`.

If you change anything under `shared-core-rs/crates/`, run that — the build
phase will warn that the lib is older than the sources, but it won't rebuild
for you (a multi-minute compile on every incremental build would be worse than
the warning).

Requires a Rust toolchain: https://rustup.rs

## See also

- `shared-core-rs/README.md` — crate layout and binding generation
- `app/windows/HyperWhisper/Resources/rust-core/` — the Windows equivalent,
  which has always tracked only `.gitkeep` placeholders
