# `hw-releasenotes` fuzz target

Release notes come from a remote appcast feed, and the shared-core release
profile sets `panic = "abort"`. A panic in this parser is therefore a hard crash
on a hostile or merely malformed feed — no unwinding, no Sentry breadcrumb. This
target exists to keep that from happening.

## Run it

```bash
cd shared-core-rs/crates/hw-releasenotes/fuzz
cargo +nightly fuzz run parse -- -max_total_time=300
```

The `+nightly` is **required**. The ancestor `shared-core-rs/rust-toolchain.toml`
pins 1.86 even down here, and cargo-fuzz needs nightly's `-Z` sanitizer flags.

First-time setup, if `cargo fuzz` is not installed:

```bash
rustup toolchain install nightly
cargo +nightly install cargo-fuzz --locked   # +nightly required: cargo-fuzz's
                                             # own lock pins a dep needing 1.91
```

Building the target also needs a C++ compiler for libFuzzer (`apt-get install
g++` on Debian/Ubuntu), or `cc-rs` fails with `failed to find tool "c++"`.

## What it checks

Beyond "does not panic", the target asserts three invariants on every input:

1. `plain_text(html)` equals the concatenation of `parse_inline(html)`'s run
   texts — the two entry points may not drift apart.
2. No run is ever empty (`flush` drops an empty buffer).
3. Every surviving `link` carries an allow-listed scheme (`http`, `https`,
   `mailto`). This is the security property the whole crate exists to hold.

Both `collapse_whitespace` modes run off the one corpus.

## Corpus

`corpus/parse/` is committed on purpose, and the `.gitignore` un-ignores it.
cargo-fuzz's generated `.gitignore` excludes `corpus/` wholesale, but CI never
runs this target — so an uncommitted corpus would mean every local run starts
from nothing.

The 112 seeds are transliterated from every case in both native oracle suites:

- `app/macos/hyperwhisperTests/ReleaseNotesHTMLTests.swift`
- the `InlineHtml` block of `app/windows/HyperWhisper.SmokeTests/Program.cs`

plus the two entity drift cases from issue #284 (`&#+65;`, `&#x 41;`), degenerate
and multi-scalar-grapheme shapes, and fragments of the two live feeds
(`nextjs/public/appcast.xml`, `nextjs/public/appcast-windows.xml`).

Machine-found additions are **not** committed; only the curated seeds are.

## CI does not run this

Deliberately. It needs nightly, a network fetch for `libfuzzer-sys`, and a C++
toolchain — none of which the shared-core or Linux CI jobs have. This package
also sits outside the Cargo workspace (`exclude` in `shared-core-rs/Cargo.toml`
plus its own empty `[workspace]` table) so that `libfuzzer-sys` and `arbitrary`
never enter the offline resolve or `shared-core-rs/Cargo.lock`.
