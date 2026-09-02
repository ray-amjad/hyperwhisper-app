//! Request-size limits: the two caps every head must enforce, the base64
//! expansion that derives from one of them, and the two failure envelopes they
//! produce.
//!
//! # Why the numbers live here
//!
//! Issue #375: the macOS Local API server builds its `HTTPServer` with a
//! timeout and nothing else, and every write endpoint reads the whole body into
//! memory with `try await request.bodyData`. A 200 MB `POST` is buffered in
//! full, then amplified by the base64 decode, and only *then* rejected — peak
//! resident memory is chosen by the caller. The Linux head already had the cap
//! (`PortableLocalApi.cs:40/185/193`); macOS never did.
//!
//! The obvious fix is a constant in the macOS server. That is how the three
//! heads ended up with three different origin guards (#289): each one gets its
//! own number, they drift, and a client that works against one head is refused
//! by another for a body the docs never described. So the numbers live in Rust
//! and every head reads them, exactly as [`crate::TOKEN_ENTROPY_BYTES`] is the
//! one place `32` is written down.
//!
//! [`MAX_REQUEST_BYTES`] and [`MAX_UPLOAD_BYTES`] are the values
//! `PortableLocalApiOptions` already ships
//! (`app/shared-dotnet/HyperWhisper.LocalApi/PortableLocalApi.cs:18-19`). They
//! are not new policy — moving them here is what makes them shared. Changing
//! either one changes what every head accepts.
//!
//! # The failures are business failures: HTTP 200, `INVALID_REQUEST`
//!
//! Not `413`, and not a `PAYLOAD_TOO_LARGE` code. `PAYLOAD_TOO_LARGE` is one of
//! the four codes Linux emitted outside the closed 14 (see `failure.rs`), and a
//! client sharing the macOS `Codable` decoder cannot decode *any* envelope
//! carrying it. The issue's suggested fix asks for a 413; taking it literally
//! would trade an availability bug for a contract bug. The Linux head already
//! returns 200 + `INVALID_REQUEST` here, so 200 is also what the other heads
//! must send for the responses to be the same response.
//!
//! # Arithmetic that cannot abort
//!
//! Every function here takes a length that comes off the wire — a
//! `Content-Length`, a running byte counter, the length of a base64 string a
//! caller sent. Under the workspace's `panic = "abort"` an arithmetic overflow
//! is not an HTTP 500, it is the whole HyperWhisper process going away, which
//! is a worse availability bug than the one being fixed. So the base64
//! expansion saturates rather than wrapping: `(u64::MAX + 2)` overflows, and
//! `u64::MAX` is a length a caller can claim.

use crate::failure::{Failure, LocalApiErrorCode};

/// The largest request body any head accepts, in bytes: 50 MiB.
///
/// The value of `PortableLocalApiOptions.MaxRequestBytes`
/// (`PortableLocalApi.cs:18`). It bounds the *whole* body — JSON envelope,
/// multipart wrapper and all — which is why it is larger than
/// [`MAX_UPLOAD_BYTES`].
pub const MAX_REQUEST_BYTES: u64 = 52_428_800;

/// The largest piece of audio any head accepts, in bytes: 48 MiB.
///
/// The value of `PortableLocalApiOptions.MaxUploadBytes`
/// (`PortableLocalApi.cs:19`). Applies to the decoded bytes of an
/// `audio_base64` payload and to a multipart `audio` part — the two shapes a
/// head buffers whole. Roughly 25 minutes of 16 kHz mono 16-bit WAV.
pub const MAX_UPLOAD_BYTES: u64 = 50_331_648;

/// The longest base64 string that can decode to [`MAX_UPLOAD_BYTES`] or fewer
/// bytes.
///
/// `ceil(n / 3) * 4`, written as `(n + 2) / 3 * 4` — the expansion
/// `PortableLocalApi.cs:244` computes inline. Checking it *before*
/// `Data(base64Encoded:)` / `Convert.FromBase64String` is the half of #375 that
/// stops the amplification: a caller cannot make the head allocate the decoded
/// buffer just to be told the decoded buffer is too big.
///
/// Computed against [`MAX_UPLOAD_BYTES`], **not** [`MAX_REQUEST_BYTES`]. The
/// .NET head does the same, and a head that used the other cap would accept
/// payloads its sibling rejects.
///
/// Saturating: this is `const fn` over a fixed input today, but it is written
/// so it stays total if the cap ever becomes a parameter. See the module docs
/// on `panic = "abort"`.
#[must_use]
pub const fn max_base64_length_for_upload() -> u64 {
    base64_encoded_length(MAX_UPLOAD_BYTES)
}

/// How many base64 characters `decoded_len` bytes encode to, padded:
/// `ceil(decoded_len / 3) * 4`.
///
/// Saturates instead of overflowing. `u64::MAX` in gives `u64::MAX` out, which
/// is the right answer for a bound: no real string is that long, so nothing is
/// rejected that should have been accepted.
const fn base64_encoded_length(decoded_len: u64) -> u64 {
    // `+ 2` overflows at `u64::MAX - 1` and `* 4` overflows above
    // `u64::MAX / 4`, so both saturate. The `/ 3` cannot overflow or divide by
    // zero, so it stays a plain divide.
    (decoded_len.saturating_add(2) / 3).saturating_mul(4)
}

/// Whether a request body of `len` bytes is over [`MAX_REQUEST_BYTES`].
///
/// Strictly greater, so a body of exactly the cap is accepted — the same
/// comparison `PortableLocalApi.cs:185` makes. Call it twice on a streaming
/// read: once on the declared `Content-Length` (reject before allocating
/// anything) and again on the running counter (a chunked body declares no
/// length, and a declared one can lie).
#[must_use]
pub const fn exceeds_request_limit(len: u64) -> bool {
    len > MAX_REQUEST_BYTES
}

/// Whether a piece of audio of `len` bytes is over [`MAX_UPLOAD_BYTES`].
///
/// Strictly greater, matching `PortableLocalApi.cs:193`/`:251`.
#[must_use]
pub const fn exceeds_upload_limit(len: u64) -> bool {
    len > MAX_UPLOAD_BYTES
}

/// Whether a base64 string of `encoded_len` characters is over what
/// [`MAX_UPLOAD_BYTES`] permits.
///
/// The pre-decode check. `encoded_len` is the length of the **trimmed** string,
/// as `PortableLocalApi.cs:243-244` measures it.
#[must_use]
pub const fn exceeds_base64_upload_limit(encoded_len: u64) -> bool {
    encoded_len > max_base64_length_for_upload()
}

/// The response an over-sized request body gets, byte for byte what the Linux
/// head already sends (`PortableLocalApi.cs:185`).
///
/// HTTP 200 carrying `INVALID_REQUEST` — see the module docs for why this is
/// not a 413.
#[must_use]
pub fn request_too_large() -> Failure {
    Failure::business(
        LocalApiErrorCode::InvalidRequest,
        "Request exceeds the configured limit.",
    )
}

/// The response an over-sized audio upload gets, byte for byte what the Linux
/// head already sends (`PortableLocalApi.cs:193`, `:245`, `:251`).
///
/// One message for all three sites — the multipart part, the base64 string
/// before decoding, and the decoded bytes — because a caller cannot tell them
/// apart and the .NET head does not either.
#[must_use]
pub fn upload_too_large() -> Failure {
    Failure::business(
        LocalApiErrorCode::InvalidRequest,
        "Audio exceeds the configured upload limit.",
    )
}

#[cfg(test)]
mod tests {
    use super::{
        base64_encoded_length, exceeds_base64_upload_limit, exceeds_request_limit,
        exceeds_upload_limit, max_base64_length_for_upload, request_too_large, upload_too_large,
        MAX_REQUEST_BYTES, MAX_UPLOAD_BYTES,
    };
    use crate::failure::LocalApiErrorCode;

    /// The two numbers, spelled out a second time. They are a wire contract
    /// shared with `PortableLocalApiOptions` (`PortableLocalApi.cs:18-19`) and
    /// with `PortableLocalApiHost` (`LocalApiHost.cs:59-60`); a head that
    /// enforces a different cap accepts a body its sibling refuses. Editing the
    /// constants without editing this test is the mistake worth catching.
    #[test]
    fn the_caps_are_the_dotnet_numbers() {
        assert_eq!(MAX_REQUEST_BYTES, 52_428_800);
        assert_eq!(MAX_UPLOAD_BYTES, 50_331_648);
        // 50 MiB and 48 MiB, said the other way round.
        assert_eq!(MAX_REQUEST_BYTES, 50 * 1024 * 1024);
        assert_eq!(MAX_UPLOAD_BYTES, 48 * 1024 * 1024);
    }

    /// `PortableLocalApiOptions.Validate()` throws when
    /// `MaxUploadBytes > MaxRequestBytes` (`PortableLocalApi.cs:27`). The pair
    /// now lives in one place, so assert the invariant in that place: an upload
    /// cap above the request cap would be unreachable, and every oversized
    /// upload would be reported as an oversized *request* instead.
    ///
    /// Phrased through the predicates rather than as `MAX_UPLOAD_BYTES <=
    /// MAX_REQUEST_BYTES`: clippy's `assertions_on_constants` rejects an
    /// `assert!` over two consts, and "an upload at the cap is an acceptable
    /// request" is the property a head actually depends on anyway.
    #[test]
    fn the_upload_cap_fits_inside_the_request_cap() {
        assert!(!exceeds_request_limit(MAX_UPLOAD_BYTES));
        assert_ne!(MAX_UPLOAD_BYTES, 0);
        assert_ne!(MAX_REQUEST_BYTES, 0);
    }

    /// The cap is inclusive on both predicates: `>` , not `>=`. Both .NET
    /// comparisons are `>` (`:185`, `:193`), so a body of exactly the cap is
    /// accepted, and an off-by-one here is a body one head takes and another
    /// refuses.
    #[test]
    fn the_boundaries_are_exclusive_at_the_cap() {
        assert!(!exceeds_request_limit(0));
        assert!(!exceeds_request_limit(MAX_REQUEST_BYTES - 1));
        assert!(!exceeds_request_limit(MAX_REQUEST_BYTES));
        assert!(exceeds_request_limit(MAX_REQUEST_BYTES + 1));
        assert!(exceeds_request_limit(u64::MAX));

        assert!(!exceeds_upload_limit(0));
        assert!(!exceeds_upload_limit(MAX_UPLOAD_BYTES - 1));
        assert!(!exceeds_upload_limit(MAX_UPLOAD_BYTES));
        assert!(exceeds_upload_limit(MAX_UPLOAD_BYTES + 1));
        assert!(exceeds_upload_limit(u64::MAX));
    }

    /// The expansion is `((long)MaxUploadBytes + 2) / 3 * 4`, evaluated exactly
    /// as C# evaluates it — integer division before the multiply, so the two
    /// heads compute the same threshold to the character.
    ///
    /// `manual_div_ceil` is allowed here on purpose: the point of the line is
    /// to be a character-for-character transcription of the C# expression, so
    /// a reader can diff it against `PortableLocalApi.cs:244`. Rewriting it as
    /// `div_ceil` would make the test agree with the implementation by
    /// construction and stop testing anything.
    #[test]
    #[allow(clippy::manual_div_ceil)]
    fn the_base64_expansion_matches_the_dotnet_formula() {
        let dotnet = (MAX_UPLOAD_BYTES + 2) / 3 * 4;
        assert_eq!(max_base64_length_for_upload(), dotnet);
        assert_eq!(max_base64_length_for_upload(), 67_108_864);
    }

    /// `ceil(n / 3) * 4` on the four residues around a 3-byte group, plus the
    /// empty string. `+ 2` before the divide is what makes 1 and 2 bytes round
    /// up to a full 4-character group, which is where a hand-written
    /// `n / 3 * 4` would be wrong.
    #[test]
    fn the_expansion_rounds_up_to_whole_base64_groups() {
        assert_eq!(base64_encoded_length(0), 0);
        assert_eq!(base64_encoded_length(1), 4);
        assert_eq!(base64_encoded_length(2), 4);
        assert_eq!(base64_encoded_length(3), 4);
        assert_eq!(base64_encoded_length(4), 8);
        assert_eq!(base64_encoded_length(6), 8);
        assert_eq!(base64_encoded_length(7), 12);
    }

    /// The lengths come off the wire, and the release profile is
    /// `panic = "abort"`: an overflow here would not be a 500, it would take
    /// the app down from a `Content-Length` header. `u64::MAX + 2` overflows
    /// and `_ * 4` overflows well before that, so both saturate.
    #[test]
    fn the_expansion_saturates_instead_of_overflowing() {
        assert_eq!(base64_encoded_length(u64::MAX), u64::MAX);
        assert_eq!(base64_encoded_length(u64::MAX - 1), u64::MAX);
        assert_eq!(base64_encoded_length(u64::MAX - 2), u64::MAX);

        // The multiply is what saturates first. Straddle its exact threshold:
        // the smallest input whose expansion does not fit in a `u64`, and the
        // largest one that still does.
        let first_saturating = (u64::MAX / 4 + 1) * 3 - 2;
        assert_eq!(base64_encoded_length(first_saturating), u64::MAX);
        assert!(base64_encoded_length(first_saturating - 1) < u64::MAX);

        // And everything over the cap is still reported as over the cap after
        // saturating, which is the property that matters on the wire.
        assert!(exceeds_base64_upload_limit(base64_encoded_length(u64::MAX)));
    }

    /// The base64 predicate's own boundary, and the property that ties it to
    /// the decoded cap: a string at the threshold decodes to at most
    /// `MAX_UPLOAD_BYTES`, so the pre-check never rejects a payload the
    /// post-decode check would have accepted.
    #[test]
    fn the_base64_predicate_brackets_the_upload_cap() {
        let threshold = max_base64_length_for_upload();
        assert!(!exceeds_base64_upload_limit(0));
        assert!(!exceeds_base64_upload_limit(threshold - 1));
        assert!(!exceeds_base64_upload_limit(threshold));
        assert!(exceeds_base64_upload_limit(threshold + 1));
        assert!(exceeds_base64_upload_limit(u64::MAX));

        // 4 base64 characters carry at most 3 bytes, so the threshold admits
        // no more than the upload cap once decoded.
        assert!(threshold / 4 * 3 <= MAX_UPLOAD_BYTES);
        // And it is not needlessly tight: the cap itself still fits.
        assert!(!exceeds_base64_upload_limit(base64_encoded_length(
            MAX_UPLOAD_BYTES
        )));
    }

    /// Both messages are the .NET head's, verbatim
    /// (`PortableLocalApi.cs:185`, `:193`). Every head emits these bytes, so a
    /// reworded string is a wire change.
    #[test]
    fn the_failure_messages_are_the_dotnet_ones() {
        assert_eq!(
            request_too_large().message,
            "Request exceeds the configured limit."
        );
        assert_eq!(
            upload_too_large().message,
            "Audio exceeds the configured upload limit."
        );
        assert_eq!(request_too_large().hint, None);
        assert_eq!(upload_too_large().hint, None);
    }

    /// HTTP 200 carrying `INVALID_REQUEST`, on both. The issue asks for a 413;
    /// a 413 would need a fifteenth code, and `PAYLOAD_TOO_LARGE` is one of the
    /// four that make the whole envelope undecodable for a client sharing the
    /// macOS `Codable` enum. This test is the guard on that decision.
    #[test]
    fn both_failures_are_business_failures() {
        for failure in [request_too_large(), upload_too_large()] {
            assert_eq!(failure.http_status(), 200);
            assert_eq!(failure.code, LocalApiErrorCode::InvalidRequest);
            assert_eq!(
                LocalApiErrorCode::from_wire_value("PAYLOAD_TOO_LARGE"),
                None
            );
            assert!(failure.to_json().contains(r#""code":"INVALID_REQUEST""#));
        }
    }
}
