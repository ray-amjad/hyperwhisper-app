# `hw-phonetic` fuzz target

Both vocabulary passes run in the middle of a transcription, and the shared-core
release profile sets `panic = "abort"`. A panic in either one is not a failed
vocabulary pass: it is the whole HyperWhisper process going away, with the
user's dictation in it, no unwinding and no Sentry breadcrumb. This target
exists to keep that from happening.

`apply_substring_vocabulary` is the reason it exists. It folds the transcript
(NFD, drop combining marks, lowercase), searches the folded copy, and maps each
match's folded byte range back to a byte range in the **original** string —
index arithmetic across two strings whose lengths do not correspond. The crate
denies `indexing_slicing` for the same reason.

## Run it

```bash
cd shared-core-rs/crates/hw-phonetic/fuzz
cargo +nightly fuzz run vocab -- -max_total_time=300
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

## Input shape

Fields are separated by `\0`, **not** `\n` — a newline inside the transcript is
the whole point of one of the unified rules, so the corpus has to be able to
carry one.

```
<text>\0<word1>\0<replacement1>\0<word2>\0<replacement2>…
```

A replacement field of exactly `\u{1}`, or a trailing word with no field after
it, makes that row's replacement `None`. Both shapes have to be reachable:
`None` is a spelling-hint row, which is the only kind the phonetic matcher acts
on, while `Some("")` takes a different branch in both passes.

## What it checks

Beyond "does not panic":

1. Both passes are **pure** — the same inputs give the same answer twice, so the
   process-wide code cache cannot change a result.
2. `entry_count` never exceeds the number of rows given; the build filters only
   drop.
3. Every reported match replaced a token with one of the vocabulary words, as
   the core normalized it. A replacement outside that set would mean the matcher
   invented text.
4. With no usable row, the matcher's only change to the text is the NFC
   normalization it documents.
5. **With no usable row, the substring pass returns the text BYTE-IDENTICAL** —
   not normalized, not case-folded, not stripped of its accents. This is the
   invariant the whole offset map exists to hold: the folded copy is a search
   index, never an output. A port that returned the folded string instead would
   pass every "does it replace the word" test and fail here on the first
   accented input.

## Corpus

`corpus/vocab/` is committed on purpose, and the `.gitignore` un-ignores it. The
seeds are transliterated from the decision table in
`shared-conformance/phonetic-vectors.json` plus the boundary cases in `fold.rs`
and `substring.rs` — a decomposed accent, a precomposed one, a Hangul syllable
whose fold expands one character into two, a lone combining mark that folds to
nothing, an astral character at the `<=2`-scalar gate, and `İ` for the
culture-invariant case fold.

Machine-found additions are **not** committed; only the curated seeds are.

## CI does not run this

Deliberately. It needs nightly, a network fetch for `libfuzzer-sys`, and a C++
toolchain — none of which the shared-core or Linux CI jobs have. This package
also sits outside the Cargo workspace (`exclude` in `shared-core-rs/Cargo.toml`
plus its own empty `[workspace]` table) so that `libfuzzer-sys` and `arbitrary`
never enter the offline resolve or `shared-core-rs/Cargo.lock`.
