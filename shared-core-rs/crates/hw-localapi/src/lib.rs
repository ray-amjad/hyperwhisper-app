//! `hw-localapi` — the Local API wire contract, shared by every head.
//!
//! The Local API is one documented HTTP contract with three implementations:
//! `app/macos/hyperwhisper/Managers/LocalAPI/` (FlyingFox),
//! `app/windows/HyperWhisper/Services/LocalApi/` (Kestrel) and
//! `app/shared-dotnet/HyperWhisper.LocalApi/` (Kestrel, Linux). The contract was
//! enforced by comments — "Field shapes match macOS exactly", "Mirrors the
//! macOS LocalAPIServer surface 1:1" — and it drifted anyway. Issue #289 is the
//! audit; this crate is its first step.
//!
//! # What is here, and what is not
//!
//! Seven things, and only seven:
//!
//! * [`check_origin`] — the DNS-rebind guard, which **only macOS shipped**. Two
//!   of three platforms served every route, including the unauthenticated
//!   `/health`, with no `Host` check at all.
//! * [`generate_token`] — the 32-byte → unpadded-base64url encoding all three
//!   heads wrote out separately.
//! * [`authorize`] — one constant-time compare, replacing three different
//!   behaviours (see `auth.rs` for the table).
//! * [`Failure`] and [`LocalApiErrorCode`] — the closed 14-code enum, and the
//!   rule that a business failure carries HTTP 200. Linux emitted four codes
//!   outside the enum and returned 404/413/503/408, which is precisely the
//!   failure the HTTP-200 rule exists to prevent.
//! * [`MAX_REQUEST_BYTES`] / [`MAX_UPLOAD_BYTES`], the base64 expansion
//!   [`max_base64_length_for_upload`] that derives from the second, and the two
//!   envelopes [`request_too_large`] / [`upload_too_large`] — the request-size
//!   caps **only the Linux head shipped** (#375). macOS built its `HTTPServer`
//!   with a timeout and nothing else, so a caller chose the app's peak resident
//!   memory. The numbers are `PortableLocalApiOptions`', moved rather than
//!   invented.
//! * [`resolve_engine_alias`] and [`EngineId`] — `POST /transcribe`'s `engine`
//!   field, resolved by one table instead of four hand-kept `switch`es, two of
//!   which are on macOS alone. It resolves an id and says nothing about whether
//!   this build can serve it; see `engine.rs` (#356 item 3).
//! * [`mode_key_classification`], [`REQUIRED_MODE_KEYS`],
//!   [`missing_required_mode_keys`], [`validate_mode`],
//!   [`mode_name_comparison_key`], [`mode_name_conflict`] and
//!   [`mode_name_taken_failure`] — the Mode body's key union, the create-only
//!   required set, the value bounds, and what "the same name" means. See
//!   `mode.rs` (#356 items 2 and 5).
//!
//! Deliberately NOT here: routing, JSON body parsing, file reads, the
//! `audio_base64` buffer itself and its decode, `map_transcription_error`'s
//! message table, pagination — and, since review, **the size comparisons
//! themselves**. An earlier revision exported
//! `exceeds_request_limit`, `exceeds_upload_limit` and
//! `exceeds_base64_upload_limit`; no head can call them, because macOS compares
//! against a `limit` parameter and the .NET head against a host-overridable
//! option, so a predicate hard-wired to the constants is the wrong comparison on
//! both. `limits.rs` says so at length under "What is deliberately *not* here".
//! Issue #289 lists the rest; the follow-up issue carries them. So `limits.rs`
//! contributes the numbers and the two failure envelopes, not the comparison and
//! not the buffering — everything above is still pure integer, string and byte
//! logic over header-sized inputs, which is what makes the `panic = "abort"`
//! risk acceptable — see below.
//!
//! #356 adds four more lines this crate does not cross, each for the same
//! reason — the thing on the far side is a *capability* or a *catalog*, not a
//! wire shape, and pulling it in would either be wrong on one platform or
//! duplicate something that already has a shared home:
//!
//! 1. **Whether an engine is available.** [`resolve_engine_alias`] returns the
//!    id the caller asked for. macOS has Nemotron and Apple Speech, the .NET
//!    heads do not, so one answer to "is this usable" is wrong somewhere. Each
//!    head decides, and answers `ENGINE_UNAVAILABLE` — no new code.
//! 2. **Cloud provider folding.** `openapi.yaml` documents `engine` as five
//!    names *or* a `<CloudProvider rawValue>`. The five are here; the rawValues
//!    belong to `hw-catalog`'s `CloudSttCatalog::normalize_cloud_provider`,
//!    which macOS and Windows already call. Two catalogs of the same thing is
//!    the failure #356 exists to stop.
//! 3. **NFC and Unicode general categories.** [`mode_name_comparison_key`] is
//!    `trim` + `to_lowercase`, both `std`. macOS's
//!    `precomposedStringWithCanonicalMapping` and its category-based boundary
//!    trim stay a macOS pre-step, because reproducing them needs
//!    `unicode-normalization` and a category table — and rule 2 below is why
//!    that dependency is not taken. `mode.rs` says so at length; do not "fix"
//!    it.
//! 4. **"An enabled `postProcessingMode` needs a provider".** One line on the
//!    portable head, a much richer rule on Windows that reaches into
//!    `CustomEndpointManager` and `PlatformHelper`, and nothing at all on
//!    macOS. All three keep their own.
//!
//! # Panic-free by construction
//!
//! The workspace release profile sets `panic = "abort"`, and every input here
//! is chosen by whoever is talking to the loopback socket. A panic would not be
//! an HTTP 500: it would abort the whole HyperWhisper process, with no
//! unwinding and no Sentry breadcrumb. Issue #289 names this as the strongest
//! argument against a larger scope, and it is the reason for three rules:
//!
//! 1. `Cargo.toml` denies `clippy::indexing_slicing`, `clippy::unwrap_used` and
//!    `clippy::expect_used`. All three are `clippy::restriction` lints —
//!    **allow-by-default and not part of `clippy::all`** — so CI's plain
//!    `cargo clippy --all-targets -- -D warnings` does not enable them and that
//!    table is the only thing that does.
//! 2. Zero dependencies. SHA-256, the constant-time compare and the base64url
//!    encoder are written out here rather than pulled in, because every
//!    dependency is another crate that can abort the app from one malformed
//!    header. It also keeps `cargo build --offline` resolvable with no new
//!    registry fetch.
//! 3. No entropy generation. [`generate_token`] takes the 32 bytes from the
//!    host's own CSPRNG, so an entropy failure stays a host-side error the head
//!    can recover from instead of an abort.
//!
//! `fuzz/` holds a cargo-fuzz target over the origin and header parsing. It
//! sits outside the Cargo workspace so `libfuzzer-sys` never enters the offline
//! resolve; see `fuzz/README.md`.

// The three restriction lints in `Cargo.toml` apply to every target of the
// package, tests included. Test code legitimately unwraps and indexes, so
// re-allow them there — and only there.
#![cfg_attr(
    test,
    allow(clippy::indexing_slicing, clippy::unwrap_used, clippy::expect_used)
)]

mod auth;
mod engine;
mod failure;
mod limits;
mod mode;
mod origin;
mod sha256;
mod token;

pub use auth::{authorize, bearer_token, AUTHORIZATION_HEADER};
pub use engine::{resolve_engine_alias, EngineId, ALL_ENGINE_IDS};
pub use failure::{
    forbidden_origin, unauthorized, Failure, FailureKind, LocalApiErrorCode, ALL_ERROR_CODES,
};
pub use limits::{
    max_base64_length_for_upload, request_too_large, upload_too_large, MAX_REQUEST_BYTES,
    MAX_UPLOAD_BYTES,
};
pub use mode::{
    missing_required_mode_keys, mode_key_classification, mode_name_comparison_key,
    mode_name_conflict, mode_name_taken_failure, validate_mode, ModeKeyClass, ModeOperation,
    ModeValidationInput, MODE_CUSTOM_VOCABULARY_MAX_TERMS, MODE_CUSTOM_VOCABULARY_TERM_MAX_CHARS,
    MODE_LANGUAGE_MAX_CHARS, MODE_NAME_MAX_CHARS, MODE_POST_PROCESSING_MODE_MAX,
    MODE_POST_PROCESSING_MODE_MIN, MODE_PRESET_MAX_CHARS, MODE_PROMPT_MAX_CHARS,
    MODE_SORT_ORDER_MAX, MODE_SORT_ORDER_MIN, REQUIRED_MODE_KEYS,
};
pub use origin::{check_origin, OriginDecision, OriginHeaders};
pub use token::{
    base64url_encode, generate_token, is_well_formed_token, token_fingerprint, TokenError,
    TOKEN_ENTROPY_BYTES, TOKEN_LENGTH,
};

#[cfg(test)]
mod tests {
    use super::{
        authorize, check_origin, forbidden_origin, generate_token, OriginDecision, OriginHeaders,
        TOKEN_ENTROPY_BYTES,
    };

    /// The whole request path, end to end, for the one shape that must work:
    /// a loopback client with the right token.
    ///
    /// The order matters and is the order every head must apply it in — origin
    /// guard first, on every route including `/health`, then the bearer check
    /// on the authenticated ones. A head that checks the token first tells an
    /// unauthenticated rebound page whether its guess was right.
    #[test]
    fn the_guard_runs_before_the_bearer_check() {
        let token = generate_token(&[7u8; TOKEN_ENTROPY_BYTES]).expect("32 bytes");
        let header = format!("Bearer {token}");

        let loopback = OriginHeaders {
            host: Some(String::from("127.0.0.1:51671")),
            ..OriginHeaders::default()
        };
        assert!(check_origin(&loopback, 51671).is_allowed());
        assert!(authorize(Some(&header), &token));

        // The rebound page: it never gets as far as the token, and the token it
        // sends is irrelevant to that.
        let rebound = OriginHeaders {
            host: Some(String::from("attacker.com:51671")),
            origin: Some(String::from("http://attacker.com")),
            sec_fetch_site: Some(String::from("cross-site")),
        };
        assert_eq!(check_origin(&rebound, 51671), OriginDecision::DeniedHost);
        assert_eq!(forbidden_origin().http_status(), 403);
    }
}
