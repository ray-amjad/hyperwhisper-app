//! Bearer-token encoding: 32 entropy bytes in, 43 unpadded base64url
//! characters out.
//!
//! # The crate does not generate entropy, and that is the point
//!
//! Issue #289 calls `panic = "abort"` "the strongest argument against the
//! aggressive scope", and names the entropy path specifically: `rand` appears
//! nowhere in the workspace, and under `panic = "abort"` a `rand` failure would
//! abort the whole HyperWhisper process where
//! `LocalAPIAuth.generateToken()` currently falls back to concatenated UUIDs.
//!
//! So the split is: the HOST draws 32 bytes from the platform CSPRNG
//! (`SecRandomCopyBytes` on macOS/iOS, `RandomNumberGenerator.GetBytes` on
//! .NET), decides for itself what to do when that fails, and passes the bytes
//! in. This crate only encodes. There is no `rand` dependency, and there is no
//! path from an entropy failure to an abort.
//!
//! # The encoding is the one all three heads already agreed on
//!
//! `Data(bytes).base64EncodedString()` then `=`→``, `+`→`-`, `/`→`_` on macOS;
//! `Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_')`
//! on Windows and Linux. Same output, three times. 32 bytes is 256 bits, which
//! is 42⅔ base64 symbols, so the encoding is always 43 characters with the last
//! one carrying 2 significant bits.

use crate::sha256::sha256;

/// How many bytes of entropy [`generate_token`] requires. Not a maximum or a
/// minimum: exactly this, so a caller that passes a short buffer gets an error
/// instead of a weak token.
pub const TOKEN_ENTROPY_BYTES: usize = 32;

/// The length of every generated token: `ceil(32 * 8 / 6)` = 43 characters, no
/// padding. `LocalApiTokenStore.IsValidToken` on the .NET side already checks
/// for exactly this.
pub const TOKEN_LENGTH: usize = 43;

/// The base64url alphabet, RFC 4648 §5 — the standard alphabet with `+`/`/`
/// replaced by `-`/`_` so the token drops into a URL, a JSON value or an HTTP
/// header with no escaping.
const ALPHABET: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

/// Why [`generate_token`] refused.
///
/// One variant, and it stays one variant on purpose: the only thing that can go
/// wrong in an encoder with no allocation limit and no I/O is being handed the
/// wrong number of bytes.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TokenError {
    /// The host passed something other than [`TOKEN_ENTROPY_BYTES`] bytes.
    WrongEntropyLength {
        /// Always [`TOKEN_ENTROPY_BYTES`].
        expected: usize,
        /// What the caller actually passed.
        actual: usize,
    },
}

impl core::fmt::Display for TokenError {
    fn fmt(&self, formatter: &mut core::fmt::Formatter<'_>) -> core::fmt::Result {
        match self {
            TokenError::WrongEntropyLength { expected, actual } => write!(
                formatter,
                "local API token needs exactly {expected} entropy bytes, got {actual}"
            ),
        }
    }
}

impl std::error::Error for TokenError {}

/// Encode 32 host-supplied CSPRNG bytes as a Local API bearer token.
///
/// # Errors
///
/// [`TokenError::WrongEntropyLength`] when `entropy` is not exactly
/// [`TOKEN_ENTROPY_BYTES`] bytes long. There is no other failure mode, and no
/// panic: the encoder never indexes and never allocates unboundedly.
pub fn generate_token(entropy: &[u8]) -> Result<String, TokenError> {
    if entropy.len() != TOKEN_ENTROPY_BYTES {
        return Err(TokenError::WrongEntropyLength {
            expected: TOKEN_ENTROPY_BYTES,
            actual: entropy.len(),
        });
    }
    Ok(base64url_encode(entropy))
}

/// Unpadded base64url for an arbitrary byte string.
///
/// Public because [`generate_token`]'s contract is "this encoding", and a head
/// that has to encode something else on the same wire (a digest in a log line,
/// say) should not write a second one.
#[must_use]
pub fn base64url_encode(bytes: &[u8]) -> String {
    let mut encoded = String::with_capacity(bytes.len().div_ceil(3).saturating_mul(4));
    let mut triples = bytes.chunks_exact(3);
    for triple in triples.by_ref() {
        let mut group: u32 = 0;
        for byte in triple {
            group = (group << 8) | u32::from(*byte);
        }
        encoded.push(symbol((group >> 18) as u8));
        encoded.push(symbol((group >> 12) as u8));
        encoded.push(symbol((group >> 6) as u8));
        encoded.push(symbol(group as u8));
    }

    // 1 leftover byte -> 2 characters, 2 leftover bytes -> 3. No `=` padding:
    // that is the `TrimEnd('=')` / `replacingOccurrences(of: "=")` the three
    // heads all do after encoding.
    let rest = triples.remainder();
    let first = rest.first().copied();
    let second = rest.get(1).copied();
    if let Some(first) = first {
        let group = (u32::from(first) << 16) | (u32::from(second.unwrap_or(0)) << 8);
        encoded.push(symbol((group >> 18) as u8));
        encoded.push(symbol((group >> 12) as u8));
        if second.is_some() {
            encoded.push(symbol((group >> 6) as u8));
        }
    }
    encoded
}

/// The base64url character for the low 6 bits of `sextet`.
///
/// The mask makes the index unconditionally in range, so the fallback is
/// unreachable. It is written as a fallback rather than an index because
/// `clippy::indexing_slicing` is denied crate-wide and "unreachable" is a claim
/// a future edit can quietly break.
fn symbol(sextet: u8) -> char {
    ALPHABET
        .get(usize::from(sextet & 0x3F))
        .map_or('A', |byte| char::from(*byte))
}

/// Whether `token` has the shape [`generate_token`] produces: exactly
/// [`TOKEN_LENGTH`] characters from the base64url alphabet.
///
/// This is a *shape* check, not an authorization check — it tells a host that a
/// stored credential is intact, so it can regenerate rather than serve with a
/// truncated one. `LocalApiTokenStore.IsValidToken` is the same predicate on
/// the .NET side. Never use it in place of [`crate::authorize`]: it looks only
/// at the presented value and compares nothing.
#[must_use]
pub fn is_well_formed_token(token: &str) -> bool {
    token.len() == TOKEN_LENGTH
        && token
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_'))
}

/// The SHA-256 digest of a token's UTF-8 bytes, hex-encoded lower-case.
///
/// For logs and diagnostics: it lets a support flow confirm which credential a
/// client presented without the log line carrying the credential. Nothing on
/// the request path calls it — [`crate::authorize`] compares raw digests.
#[must_use]
pub fn token_fingerprint(token: &str) -> String {
    let digest = sha256(token.as_bytes());
    let mut hex = String::with_capacity(64);
    for byte in digest {
        // `write!` to a String cannot fail, but it returns a Result and
        // `expect` is denied; push the two nibbles directly instead.
        hex.push(nibble(byte >> 4));
        hex.push(nibble(byte));
    }
    hex
}

/// The lower-case hex digit for the low 4 bits of `value`.
fn nibble(value: u8) -> char {
    match value & 0x0F {
        digit @ 0..=9 => char::from(b'0'.saturating_add(digit)),
        letter => char::from(b'a'.saturating_add(letter.saturating_sub(10))),
    }
}

#[cfg(test)]
mod tests {
    use super::{
        base64url_encode, generate_token, is_well_formed_token, token_fingerprint, TokenError,
        TOKEN_ENTROPY_BYTES, TOKEN_LENGTH,
    };

    /// RFC 4648 §10's test vectors, transliterated to the URL alphabet and
    /// stripped of padding — which is exactly what the three heads produce.
    #[test]
    fn the_rfc_4648_vectors_match() {
        assert_eq!(base64url_encode(b""), "");
        assert_eq!(base64url_encode(b"f"), "Zg");
        assert_eq!(base64url_encode(b"fo"), "Zm8");
        assert_eq!(base64url_encode(b"foo"), "Zm9v");
        assert_eq!(base64url_encode(b"foob"), "Zm9vYg");
        assert_eq!(base64url_encode(b"fooba"), "Zm9vYmE");
        assert_eq!(base64url_encode(b"foobar"), "Zm9vYmFy");
    }

    /// The two characters the URL alphabet renames. Standard base64 of
    /// `[0xFB, 0xFF]` is `+/8=` and of `[0xFF, 0xFF, 0xFF]` is `////`; both must
    /// come back with `-` and `_`, or a token breaks a URL or a header.
    #[test]
    fn the_url_alphabet_replaces_plus_and_slash() {
        assert_eq!(base64url_encode(&[0xFB, 0xFF]), "-_8");
        assert_eq!(base64url_encode(&[0xFF, 0xFF, 0xFF]), "____");
        assert_eq!(base64url_encode(&[0x00, 0x00, 0x00]), "AAAA");
        assert!(!base64url_encode(&[0xFF; 32]).contains(['+', '/', '=']));
    }

    /// The real shape: 32 bytes always yields 43 well-formed characters.
    #[test]
    fn thirty_two_bytes_makes_a_forty_three_character_token() {
        let token = generate_token(&[0u8; TOKEN_ENTROPY_BYTES]).expect("32 bytes is the contract");
        assert_eq!(token.len(), TOKEN_LENGTH);
        assert_eq!(token, "A".repeat(43));
        assert!(is_well_formed_token(&token));

        let token =
            generate_token(&[0xFFu8; TOKEN_ENTROPY_BYTES]).expect("32 bytes is the contract");
        assert_eq!(token.len(), TOKEN_LENGTH);
        assert!(is_well_formed_token(&token));

        // A distinct, non-degenerate buffer, so the vector pins the bit
        // packing and not just the length.
        let entropy: Vec<u8> = (0..32u8).collect();
        let token = generate_token(&entropy).expect("32 bytes is the contract");
        assert_eq!(token, "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8");
        assert!(is_well_formed_token(&token));
    }

    /// Every entropy length except 32 is refused, and refused rather than
    /// truncated — a short buffer that silently produced a short token would be
    /// a weak credential nobody could see.
    #[test]
    fn the_wrong_entropy_length_is_an_error_not_a_short_token() {
        for length in [0usize, 1, 16, 31, 33, 64] {
            assert_eq!(
                generate_token(&vec![0u8; length]),
                Err(TokenError::WrongEntropyLength {
                    expected: TOKEN_ENTROPY_BYTES,
                    actual: length,
                }),
                "length {length} should be refused"
            );
        }
    }

    #[test]
    fn well_formedness_rejects_the_shapes_a_broken_store_returns() {
        assert!(is_well_formed_token(&"a".repeat(43)));
        assert!(is_well_formed_token(&"-".repeat(43)));
        assert!(is_well_formed_token(&"_".repeat(43)));
        assert!(!is_well_formed_token(""));
        assert!(!is_well_formed_token(&"a".repeat(42)));
        assert!(!is_well_formed_token(&"a".repeat(44)));
        // Padding, standard-alphabet characters and whitespace all fail: a
        // stored value carrying them did not come from `generate_token`.
        assert!(!is_well_formed_token(&format!("{}=", "a".repeat(42))));
        assert!(!is_well_formed_token(&format!("{}+", "a".repeat(42))));
        assert!(!is_well_formed_token(&format!("{}/", "a".repeat(42))));
        assert!(!is_well_formed_token(&format!("{} ", "a".repeat(42))));
        assert!(!is_well_formed_token(&format!("{}é", "a".repeat(41))));
    }

    #[test]
    fn the_fingerprint_is_hex_of_the_digest() {
        assert_eq!(
            token_fingerprint(""),
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
        );
        assert_eq!(
            token_fingerprint("abc"),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );
        let fingerprint = token_fingerprint(&"A".repeat(43));
        assert_eq!(fingerprint.len(), 64);
        assert!(fingerprint.bytes().all(|byte| byte.is_ascii_hexdigit()));
        assert!(!fingerprint.contains(char::is_uppercase));
    }

    /// Arbitrary byte strings encode without panicking, and the output length
    /// is always the unpadded one.
    #[test]
    fn arbitrary_lengths_encode_to_the_unpadded_length() {
        for length in 0usize..200 {
            let bytes: Vec<u8> = (0..length).map(|index| (index % 251) as u8).collect();
            let encoded = base64url_encode(&bytes);
            assert_eq!(
                encoded.len(),
                length * 8 / 6 + usize::from(length * 8 % 6 != 0)
            );
            assert!(encoded
                .bytes()
                .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_')));
        }
    }
}
