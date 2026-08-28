//! The bearer check — one constant-time compare, replacing three divergent
//! native behaviours (issue #289).
//!
//! # What the three heads did
//!
//! | | header parse | compare |
//! |---|---|---|
//! | macOS (`LocalAPIAuth.swift:64-74`) | trim, case-insensitive `bearer `, trim the rest | hand-rolled XOR over UTF-8, **early return on a length mismatch** |
//! | Windows (`LocalApiAuth.cs`) | case-insensitive `Bearer ` prefix | `FixedTimeEquals` over UTF-8 |
//! | Linux (`PortableLocalApi.cs:71-73`) | case-insensitive `Bearer ` prefix, **no trim of the value** | `FixedTimeEquals` over SHA-256 digests |
//!
//! Three of those differences are observable. macOS returns early when the
//! lengths differ, so the length of the stored token leaks through timing even
//! though the loop that follows does not. Windows compares UTF-8 directly, so
//! `FixedTimeEquals` returns `false` immediately on a length mismatch for the
//! same reason. And Linux accepts `Bearer <token>   ` where the other two
//! reject the trailing space, so a client that works on Linux fails elsewhere.
//!
//! # What this does instead
//!
//! Hash both sides to a fixed 32 bytes, then compare the digests with no early
//! exit. The loop runs 32 iterations for every request whatever the caller
//! sent, so neither the length nor any prefix of the stored token is
//! observable. Linux already did this; macOS and Windows converge onto it.
//!
//! The header parse converges on the strictest of the three: trim the header,
//! require a case-insensitive `bearer ` prefix, then trim the value. That
//! **removes** Linux's acceptance of a trailing space, which is a deliberate
//! narrowing and the only client-visible change of the three.
//!
//! # SHA-256 is not a password hash and does not need to be
//!
//! The token is 256 bits of CSPRNG output, not a user-chosen secret, so there
//! is nothing to brute-force and no reason for a slow KDF here. The hash exists
//! only to make the compare fixed-width.

use crate::sha256::sha256;

/// The header name the check reads. Lookup is the caller's job and must be
/// case-insensitive (RFC 7230 §3.2).
pub const AUTHORIZATION_HEADER: &str = "Authorization";

/// The scheme token, matched case-insensitively per RFC 7235 §2.1.
const BEARER_PREFIX: &str = "bearer ";

/// Whether `authorization_header` presents `expected_token`.
///
/// `authorization_header` is the raw header value, or `None` when the request
/// carried no `Authorization` header at all. `expected_token` is the
/// credential the host loaded from its own store.
///
/// An empty `expected_token` always denies. A host whose credential store
/// returned nothing must not accept `Authorization: Bearer ` from anyone, and
/// leaving that to each head is exactly the kind of gap this crate exists to
/// close.
#[must_use]
pub fn authorize(authorization_header: Option<&str>, expected_token: &str) -> bool {
    if expected_token.is_empty() {
        return false;
    }
    let Some(presented) = bearer_token(authorization_header) else {
        return false;
    };
    constant_time_eq(
        &sha256(presented.as_bytes()),
        &sha256(expected_token.as_bytes()),
    )
}

/// Pull the credential out of an `Authorization` header value, or `None` when
/// the header is absent or is not a bearer header.
///
/// Exposed because the heads log "no bearer header" and "wrong token"
/// differently, and re-deriving the parse to tell them apart is how the three
/// parses drifted in the first place.
#[must_use]
pub fn bearer_token(authorization_header: Option<&str>) -> Option<&str> {
    let raw = authorization_header?.trim();
    // `get(..len)` rather than `starts_with` + slicing, because the crate
    // denies indexing and the prefix is ASCII so the byte length is the
    // character length.
    let head = raw.get(..BEARER_PREFIX.len())?;
    if !head.eq_ignore_ascii_case(BEARER_PREFIX) {
        return None;
    }
    Some(raw.get(BEARER_PREFIX.len()..).unwrap_or("").trim())
}

/// Compare two digests without an early exit.
///
/// The accumulate-then-test shape is what makes it constant time: every byte is
/// read on every call and no branch depends on a partial result.
/// [`std::hint::black_box`] stops the optimizer from noticing that it could
/// stop early once `difference` is non-zero.
fn constant_time_eq(left: &[u8; 32], right: &[u8; 32]) -> bool {
    let mut difference = 0u8;
    for (a, b) in left.iter().zip(right.iter()) {
        difference |= a ^ b;
    }
    std::hint::black_box(difference) == 0
}

#[cfg(test)]
mod tests {
    use super::{authorize, bearer_token};

    const TOKEN: &str = "wJ8kQ2mVx7Lp0RtYuIoP3aSdFgHjKlZxCvBnM4qWeRt";

    #[test]
    fn a_correct_header_authorizes() {
        assert!(authorize(Some(&format!("Bearer {TOKEN}")), TOKEN));
    }

    /// RFC 7235 makes the scheme case-insensitive, and all three heads already
    /// agreed on that much.
    #[test]
    fn the_scheme_is_case_insensitive() {
        for scheme in ["Bearer", "bearer", "BEARER", "BeArEr"] {
            assert!(
                authorize(Some(&format!("{scheme} {TOKEN}")), TOKEN),
                "scheme {scheme} should be accepted"
            );
        }
    }

    /// The token itself is not case-insensitive — base64url is case-sensitive,
    /// and folding it would throw away 43 bits of the credential.
    #[test]
    fn the_token_is_case_sensitive() {
        assert!(!authorize(
            Some(&format!("Bearer {}", TOKEN.to_uppercase())),
            TOKEN
        ));
        assert!(!authorize(
            Some(&format!("Bearer {}", TOKEN.to_lowercase())),
            TOKEN
        ));
    }

    /// Surrounding whitespace is trimmed on both the header and the value.
    /// This is the convergence: macOS did it, Linux did not.
    #[test]
    fn whitespace_around_the_header_and_the_token_is_trimmed() {
        assert!(authorize(Some(&format!("  Bearer {TOKEN}  ")), TOKEN));
        assert!(authorize(Some(&format!("Bearer  {TOKEN}")), TOKEN));
        assert!(authorize(Some(&format!("Bearer {TOKEN}\t")), TOKEN));
    }

    #[test]
    fn a_wrong_or_malformed_header_denies() {
        assert!(!authorize(None, TOKEN));
        assert!(!authorize(Some(""), TOKEN));
        assert!(!authorize(Some("Bearer"), TOKEN));
        assert!(!authorize(Some("Bearer "), TOKEN));
        assert!(!authorize(Some(TOKEN), TOKEN));
        assert!(!authorize(Some(&format!("Basic {TOKEN}")), TOKEN));
        assert!(!authorize(Some(&format!("Bearer{TOKEN}")), TOKEN));
        assert!(!authorize(Some(&format!("Bearer {TOKEN}x")), TOKEN));
        assert!(!authorize(Some(&format!("Bearer x{TOKEN}")), TOKEN));
        // A prefix of the real token must not authorize, whatever its length.
        for length in 0..TOKEN.len() {
            let prefix = TOKEN.get(..length).unwrap_or("");
            assert!(
                !authorize(Some(&format!("Bearer {prefix}")), TOKEN),
                "prefix of length {length} should be denied"
            );
        }
    }

    /// The gap this closes: a host whose credential store came back empty must
    /// not accept an empty presented token.
    #[test]
    fn an_empty_expected_token_never_authorizes() {
        assert!(!authorize(Some("Bearer "), ""));
        assert!(!authorize(Some("Bearer"), ""));
        assert!(!authorize(None, ""));
        assert!(!authorize(Some(&format!("Bearer {TOKEN}")), ""));
    }

    /// Non-ASCII and non-token bytes reach the hash rather than a panic — the
    /// header is attacker-chosen and the release profile aborts on panic.
    #[test]
    fn hostile_header_values_deny_rather_than_panic() {
        for header in [
            "Bearer \u{0}",
            "Bearer é",
            "Bearer \u{1F600}",
            "\u{1F600}",
            "Bearer",
            "bearer\u{a0}x",
            "\u{feff}Bearer x",
            "Bearer \u{0}\u{0}\u{0}",
        ] {
            assert!(!authorize(Some(header), TOKEN));
        }
        // A very long header must not be treated specially either.
        assert!(!authorize(
            Some(&format!("Bearer {}", "x".repeat(100_000))),
            TOKEN
        ));
    }

    #[test]
    fn the_parse_is_reusable_on_its_own() {
        assert_eq!(bearer_token(Some("Bearer abc")), Some("abc"));
        assert_eq!(bearer_token(Some("  bearer   abc  ")), Some("abc"));
        // `"Bearer "` and `"Bearer"` are the same header after the outer trim,
        // so neither carries the `bearer ` prefix and neither parses. macOS
        // rejects both for the same reason (`trimmed.lowercased().hasPrefix`),
        // which means an empty presented token is not reachable through this
        // parse at all.
        assert_eq!(bearer_token(Some("Bearer ")), None);
        assert_eq!(bearer_token(Some("Bearer")), None);
        assert_eq!(bearer_token(Some("Bearer   ")), None);
        assert_eq!(bearer_token(Some("Basic abc")), None);
        assert_eq!(bearer_token(Some("")), None);
        assert_eq!(bearer_token(None), None);
        // A multi-byte character shorter than the prefix must not panic on the
        // prefix read — `get(..7)` on a 2-byte string returns `None`.
        assert_eq!(bearer_token(Some("é")), None);
        // ... and one that straddles byte 7 must not split a character.
        assert_eq!(bearer_token(Some("Bearéer x")), None);
    }
}
