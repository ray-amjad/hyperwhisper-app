# `hw-localapi` fuzz target

The `Host`, `Origin` and `Authorization` headers this crate parses are chosen by
whoever is talking to the loopback socket — including, by construction, the
rebound web page the origin guard exists to stop. The shared-core release
profile sets `panic = "abort"`, so a panic in that parsing is not an HTTP 500:
it is the whole HyperWhisper process going away, with no unwinding and no Sentry
breadcrumb. This target exists to keep that from happening.

## Run it

```bash
cd shared-core-rs/crates/hw-localapi/fuzz
cargo +nightly fuzz run guard -- -max_total_time=300
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

The first two bytes are the bound port, big-endian. Everything after them is
split on `\n` into four header values, in order:

```
<port:2><host>\n<origin>\n<sec-fetch-site>\n<authorization>
```

The port is taken from the input rather than fixed so the engine can reach the
port-equality branches, including the two values that behave specially: `0`
(the server is not bound yet) and `80` (the only port at which a `Host` with no
port, or an `Origin` with no port, is accepted).

Each case runs through all eight present/absent combinations of the three guard
headers, because absence is its own branch — a missing `Host` denies, a missing
`Origin` does not.

## What it checks

Beyond "does not panic":

1. `check_origin` is pure — the same headers give the same decision twice.
2. Port 0 allows nothing.
3. An allowed request always carried a `Host`.
4. `authorize` returns true only when the header really carries the expected
   token, and an empty stored credential authorizes nothing.
5. `base64url_encode` never emits a character outside the URL alphabet — a `+`,
   `/` or `=` in a token breaks the header or the URL a wrapper builds from it.
6. `generate_token` accepts exactly 32 bytes and nothing else, and what it
   returns always passes `is_well_formed_token`.

## Corpus

`corpus/guard/` is committed on purpose, and the `.gitignore` un-ignores it.
cargo-fuzz's generated `.gitignore` excludes `corpus/` wholesale, but CI never
runs this target — so an uncommitted corpus would mean every local run starts
from nothing.

The seeds are transliterated from the decision-vector table in `src/origin.rs`,
one file per row, plus the degenerate shapes the parsers have to survive (bare
`:`, `@`, `[`, a full-width `LOCALHOST`, a signed port, an over-long bearer
value). That table is itself derived branch by branch from
`app/macos/hyperwhisper/Managers/LocalAPI/LocalAPIOriginGuard.swift`, so the
corpus and the unit tests cover the same ground and stay in step.

Machine-found additions are **not** committed; only the curated seeds are.

## CI does not run this

Deliberately. It needs nightly, a network fetch for `libfuzzer-sys`, and a C++
toolchain — none of which the shared-core or Linux CI jobs have. This package
also sits outside the Cargo workspace (`exclude` in `shared-core-rs/Cargo.toml`
plus its own empty `[workspace]` table) so that `libfuzzer-sys` and `arbitrary`
never enter the offline resolve or `shared-core-rs/Cargo.lock`.
