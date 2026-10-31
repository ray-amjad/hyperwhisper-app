//! License validation: build the validate request, parse the response, and map
//! the result onto a [`LicenseStatus`]. Sans-I/O — Rust builds a [`ValidateRequest`]
//! *value* and parses a [`ValidateResponse`] *value*; the platform performs the
//! HTTP round-trip.
//!
//! Parity target is the shipped macOS (`LicenseNetworkService.swift`) and Windows
//! (`LicenseNetworkService.cs`) behavior. Where they diverge we prefer macOS (the
//! verified platform) and note the unification choice in a comment.

use serde::Deserialize;

use crate::LicenseStatus;

/// Production validate endpoint. Mirrors macOS
/// `NetworkConfig.baseURL + licenseValidateEndpoint` and Windows
/// `BaseUrl + ValidateEndpoint`.
pub const VALIDATE_URL: &str = "https://www.hyperwhisper.com/api/license/validate";

/// A fully-described license-validate HTTP request for the platform to execute.
///
/// This is a self-contained shape (hw-license is a leaf crate with no `hw-net`
/// dependency). `hw-core` mirrors it / re-maps it onto the shared net contract in
/// Wave 2 if call sites want a single HTTP type.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ValidateRequest {
    /// Always POST.
    pub url: String,
    /// `Content-Type: application/json`.
    pub content_type: String,
    /// JSON body bytes: `{"license_key","device_id","device_name"}`.
    pub body: Vec<u8>,
}

impl ValidateRequest {
    /// Body decoded as UTF-8 (lossy) — convenience for tests/logging.
    pub fn body_text(&self) -> std::borrow::Cow<'_, str> {
        String::from_utf8_lossy(&self.body)
    }
}

/// Build the POST `/api/license/validate` request.
///
/// The license key is trimmed of surrounding whitespace before being sent —
/// matches macOS (`trimmingCharacters(in: .whitespacesAndNewlines)`) and Windows
/// (`Trim()`). `device_id` is supplied by the platform (native machine id) and
/// passed through verbatim; `device_name` is the host name.
///
/// Divergence note: macOS sends `device_name`; the shipped Windows client sends
/// only `{license_key, device_id}`. The M3 contract (and this builder) always
/// include `device_name` — unifying on the macOS shape — so Windows gains the
/// field when it adopts the shared core.
pub fn build_validate_request(
    license_key: &str,
    device_id: &str,
    device_name: &str,
) -> ValidateRequest {
    let trimmed = license_key.trim();
    // Hand-rolled JSON object with the three string fields. Field order is fixed
    // (license_key, device_id, device_name) so the request is byte-deterministic
    // and golden-testable.
    let body = format!(
        "{{\"license_key\":{},\"device_id\":{},\"device_name\":{}}}",
        json_string(trimmed),
        json_string(device_id),
        json_string(device_name),
    );
    ValidateRequest {
        url: VALIDATE_URL.to_string(),
        content_type: "application/json".to_string(),
        body: body.into_bytes(),
    }
}

/// Encode a Rust string as a JSON string literal (with surrounding quotes).
/// Uses `serde_json` so escaping matches the platform JSON encoders exactly.
fn json_string(s: &str) -> String {
    serde_json::to_string(s).expect("string serialization is infallible")
}

/// The parsed validate-endpoint response. A superset of what the two platforms
/// read so either call site can be served:
/// - macOS reads `valid` (bool) and `expired` (bool), plus `error`.
/// - Windows reads `status` (string: "active"/"expired"/"revoked"/"invalid"),
///   `customer_id`, `customer_email`, `subscription_id`, `expires_at`, `error`.
///
/// All fields are optional; absent fields deserialize to `None`/`false`.
#[derive(Debug, Clone, Default, PartialEq, Deserialize)]
pub struct ValidateResponse {
    /// macOS primary signal: license is valid + active.
    #[serde(default)]
    pub valid: bool,
    /// macOS secondary signal: a known-but-expired license.
    #[serde(default)]
    pub expired: bool,
    /// Windows primary signal: raw status string.
    #[serde(default)]
    pub status: Option<String>,
    #[serde(default)]
    pub customer_id: Option<String>,
    #[serde(default)]
    pub customer_email: Option<String>,
    #[serde(default)]
    pub subscription_id: Option<String>,
    /// ISO-8601 expiry, when the server reports one.
    #[serde(default)]
    pub expires_at: Option<String>,
    /// Human-readable error message on failure.
    #[serde(default)]
    pub error: Option<String>,
}

/// Outcome of a validation attempt, ready for the platform to apply to UI state
/// and persist. Mirrors the fields macOS `LicenseValidationResult` and Windows
/// `LicenseValidationResult` expose.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ValidationOutcome {
    /// Whether the license is valid + active (user gets licensed features).
    pub is_valid: bool,
    /// The resolved status driving UI + limit enforcement.
    pub status: LicenseStatus,
    pub customer_id: Option<String>,
    pub customer_email: Option<String>,
    pub expires_at: Option<String>,
    /// Error message when validation failed (or offline-fallback note).
    pub error_message: Option<String>,
    /// Whether the server stated a verdict about this key (issue #2916).
    ///
    /// `true` for a decoded validate reply and for [`http_error_outcome`].
    /// `false` for a 200 whose body is not the validate JSON (a captive-portal
    /// login page, a proxy's HTML, an empty body), for
    /// [`crate::cache::offline_fallback_outcome`] and for [`empty_key_outcome`]:
    /// none of those is the server's word on the key. A head persists an
    /// outcome through [`crate::cache::persist_validation_verdict`] only when
    /// this is `true`; on `false` it takes its network-failure path (offline
    /// fallback to the cached verdict), so the cache is left untouched and the
    /// next check retries.
    pub is_verdict: bool,
}

/// An empty/whitespace-only license key — rejected before any network call.
/// Matches macOS ("License key cannot be empty") and Windows guard clauses.
pub fn empty_key_outcome() -> ValidationOutcome {
    ValidationOutcome {
        is_valid: false,
        status: LicenseStatus::Invalid,
        customer_id: None,
        customer_email: None,
        expires_at: None,
        error_message: Some("License key cannot be empty".to_string()),
        // Rejected before any request: the server said nothing about the key.
        is_verdict: false,
    }
}

/// Outcome for a non-200 HTTP response (after retries are exhausted by the
/// platform). `status_code` is the HTTP code; `error_message` is the server's
/// `error` field when the body is JSON, else a generic message.
///
/// Mirrors macOS `parseErrorMessage` + the non-200 branch, and Windows' status
/// switch. The license status becomes `Invalid` for a hard non-200 (the server
/// rejected the request) — Windows maps 401/other → Invalid; macOS returns
/// `.invalid` too. Transient 5xx/429 are the *platform's* retry responsibility
/// and should not reach this function as a terminal result.
pub fn http_error_outcome(status_code: u16, body: &[u8]) -> ValidationOutcome {
    let message = parse_error_message(body)
        .unwrap_or_else(|| format!("Server error (HTTP {status_code})"));
    ValidationOutcome {
        is_valid: false,
        status: LicenseStatus::Invalid,
        customer_id: None,
        customer_email: None,
        expires_at: None,
        error_message: Some(message),
        // A terminal non-200 stays a verdict here; whether a 4xx should be one
        // is #2060, not this function's call.
        is_verdict: true,
    }
}

/// Parse a 200-OK validate response body and map it to a [`ValidationOutcome`].
///
/// Mapping (unified across platforms):
/// 1. If an explicit `status` string is present, it wins (Windows behavior):
///    "active" → Active, "expired" → Expired, "revoked"/"invalid" → Invalid.
///    Any other non-empty string falls through to the boolean signals.
/// 2. Otherwise use the macOS booleans: `valid` → Active; else `expired` →
///    Expired; else Invalid.
///
/// A 200 whose body is not the validate JSON is **not a verdict** (#2916): a
/// captive portal or proxy answering 200 with its own login page, or an empty
/// body, says nothing about the key. Such a body returns an `Invalid` outcome
/// with the "Invalid server response" message (so a head that ignores the flag
/// degrades no worse than before) and `is_verdict: false`, which tells the head
/// to take its network-failure path instead of caching the status.
///
/// "The validate JSON" means a JSON object carrying `valid` and/or `status`
/// that decodes into [`ValidateResponse`]. Every 200 the validate route sends
/// carries `valid` (`nextjs/app/api/license/validate/route.ts`: each 200 is
/// `{ valid: true, ... }`; every `valid: false` reply is a 4xx/5xx), so a 200
/// whose JSON has neither key (`{}`, `[]`, `"ok"`, `null`) is not from the
/// route either. A real `{"valid":false}` or `{"status":"revoked"}` stays an
/// `Invalid` verdict.
pub fn parse_validate_response(body: &[u8]) -> ValidationOutcome {
    match decode_validate_body(body) {
        Some(resp) => outcome_from_response(&resp),
        None => ValidationOutcome {
            is_valid: false,
            status: LicenseStatus::Invalid,
            customer_id: None,
            customer_email: None,
            expires_at: None,
            error_message: Some("Invalid server response".to_string()),
            is_verdict: false,
        },
    }
}

/// Decode a 200 body as the validate reply: a JSON object that names `valid`
/// or `status` and decodes into [`ValidateResponse`]. `None` for anything else.
///
/// The struct is decoded straight from the bytes, so a duplicated field
/// (`{"valid":false,"valid":true}`) is still rejected as malformed, exactly as
/// before #2916. Going through `serde_json::Value` first would keep only the
/// last duplicate and could turn a malformed body into an Active verdict. The
/// key-presence check is a separate pass and decides nothing on its own.
fn decode_validate_body(body: &[u8]) -> Option<ValidateResponse> {
    let resp: ValidateResponse = serde_json::from_slice(body).ok()?;
    let object: serde_json::Map<String, serde_json::Value> = serde_json::from_slice(body).ok()?;
    if !object.contains_key("valid") && !object.contains_key("status") {
        return None;
    }
    Some(resp)
}

/// Map an already-parsed [`ValidateResponse`] to a [`ValidationOutcome`].
pub fn outcome_from_response(resp: &ValidateResponse) -> ValidationOutcome {
    let status = map_status(resp);
    let is_valid = status == LicenseStatus::Active;
    ValidationOutcome {
        is_valid,
        status,
        customer_id: resp.customer_id.clone(),
        customer_email: resp.customer_email.clone(),
        expires_at: resp.expires_at.clone(),
        // Only surface an error on a non-active result, matching macOS (which
        // sets errorMessage only when !isValid).
        error_message: if is_valid {
            None
        } else {
            resp.error.clone()
        },
        is_verdict: true,
    }
}

/// Map a parsed response onto a [`LicenseStatus`]. See [`parse_validate_response`]
/// for the precedence rules.
fn map_status(resp: &ValidateResponse) -> LicenseStatus {
    if let Some(raw) = resp.status.as_deref() {
        match raw.to_ascii_lowercase().as_str() {
            "active" => return LicenseStatus::Active,
            "expired" => return LicenseStatus::Expired,
            "revoked" | "invalid" => return LicenseStatus::Invalid,
            // Unknown status string → fall through to boolean signals (Windows'
            // `_ => IsValid ? Active : Invalid`, but we also honor `expired`).
            _ => {}
        }
    }
    if resp.valid {
        LicenseStatus::Active
    } else if resp.expired {
        LicenseStatus::Expired
    } else {
        LicenseStatus::Invalid
    }
}

/// Extract the `error` field from a JSON body, if present and a string.
fn parse_error_message(body: &[u8]) -> Option<String> {
    let resp: ValidateResponse = serde_json::from_slice(body).ok()?;
    resp.error
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn builds_post_body_with_trimmed_key_and_fixed_field_order() {
        let req = build_validate_request("  KEY-123  ", "device-abc", "Ray's Mac");
        assert_eq!(req.url, VALIDATE_URL);
        assert_eq!(req.content_type, "application/json");
        assert_eq!(
            req.body_text(),
            r#"{"license_key":"KEY-123","device_id":"device-abc","device_name":"Ray's Mac"}"#
        );
    }

    #[test]
    fn builds_body_escaping_special_chars() {
        let req = build_validate_request("a\"b", "id\\1", "name\nline");
        assert_eq!(
            req.body_text(),
            r#"{"license_key":"a\"b","device_id":"id\\1","device_name":"name\nline"}"#
        );
    }

    #[test]
    fn macos_valid_bool_maps_to_active() {
        let out = parse_validate_response(br#"{"valid":true}"#);
        assert_eq!(out.status, LicenseStatus::Active);
        assert!(out.is_valid);
        assert!(out.error_message.is_none());
    }

    #[test]
    fn macos_expired_bool_maps_to_expired() {
        let out = parse_validate_response(br#"{"valid":false,"expired":true,"error":"expired"}"#);
        assert_eq!(out.status, LicenseStatus::Expired);
        assert!(!out.is_valid);
        assert_eq!(out.error_message.as_deref(), Some("expired"));
    }

    #[test]
    fn macos_not_valid_not_expired_maps_to_invalid() {
        let out = parse_validate_response(br#"{"valid":false}"#);
        assert_eq!(out.status, LicenseStatus::Invalid);
    }

    #[test]
    fn windows_status_string_takes_precedence() {
        // status="active" wins even if valid is absent.
        let out = parse_validate_response(
            br#"{"status":"active","customer_id":"cust_1","expires_at":"2030-01-01T00:00:00Z"}"#,
        );
        assert_eq!(out.status, LicenseStatus::Active);
        assert!(out.is_valid);
        assert_eq!(out.customer_id.as_deref(), Some("cust_1"));
        assert_eq!(out.expires_at.as_deref(), Some("2030-01-01T00:00:00Z"));
    }

    #[test]
    fn windows_revoked_maps_to_invalid() {
        let out = parse_validate_response(br#"{"status":"revoked","valid":true}"#);
        // status string wins over the (contradictory) valid=true.
        assert_eq!(out.status, LicenseStatus::Invalid);
        assert!(!out.is_valid);
    }

    #[test]
    fn windows_status_case_insensitive() {
        let out = parse_validate_response(br#"{"status":"EXPIRED"}"#);
        assert_eq!(out.status, LicenseStatus::Expired);
    }

    #[test]
    fn unknown_status_falls_through_to_booleans() {
        let out = parse_validate_response(br#"{"status":"pending","valid":true}"#);
        assert_eq!(out.status, LicenseStatus::Active);
    }

    #[test]
    fn malformed_json_is_invalid_but_not_a_verdict() {
        let out = parse_validate_response(b"not json");
        assert_eq!(out.status, LicenseStatus::Invalid);
        assert!(!out.is_valid);
        assert_eq!(out.error_message.as_deref(), Some("Invalid server response"));
        assert!(!out.is_verdict);
    }

    #[test]
    fn undecodable_200_bodies_are_not_verdicts() {
        // #2916: a captive portal, an empty body, JSON that is not the validate
        // reply (neither `valid` nor `status`), and a validate-shaped object with
        // a field of the wrong type.
        let bodies: [&[u8]; 10] = [
            b"<html><body>Please log in to Hotel WiFi</body></html>",
            b"",
            b"   ",
            b"{}",
            b"[]",
            b"\"ok\"",
            b"null",
            b"true",
            br#"{"error":"nope"}"#,
            br#"{"valid":"yes"}"#,
        ];
        for body in bodies {
            let out = parse_validate_response(body);
            let shown = String::from_utf8_lossy(body);
            assert!(!out.is_verdict, "{shown:?} was a verdict");
            assert_eq!(out.status, LicenseStatus::Invalid, "{shown:?}");
            assert!(!out.is_valid, "{shown:?}");
            assert_eq!(
                out.error_message.as_deref(),
                Some("Invalid server response"),
                "{shown:?}"
            );
        }
    }

    #[test]
    fn duplicate_fields_are_a_non_verdict_not_active() {
        // A duplicated key is malformed. It must never become the last
        // duplicate's verdict (review of #2916: `valid` false then true).
        let bodies: [&[u8]; 3] = [
            br#"{"valid":false,"valid":true}"#,
            br#"{"valid":true,"valid":false}"#,
            br#"{"status":"revoked","status":"active"}"#,
        ];
        for body in bodies {
            let out = parse_validate_response(body);
            let shown = String::from_utf8_lossy(body);
            assert!(!out.is_verdict, "{shown:?} was a verdict");
            assert_eq!(out.status, LicenseStatus::Invalid, "{shown:?}");
            assert!(!out.is_valid, "{shown:?}");
        }
    }

    #[test]
    fn decoded_validate_replies_are_verdicts() {
        let bodies: [&[u8]; 6] = [
            br#"{"valid":true}"#,
            br#"{"valid":false}"#,
            br#"{"valid":false,"expired":true}"#,
            br#"{"status":"revoked"}"#,
            br#"{"status":"invalid"}"#,
            br#"{"status":"active","customer_id":"cust_1"}"#,
        ];
        for body in bodies {
            let out = parse_validate_response(body);
            assert!(out.is_verdict, "{:?}", String::from_utf8_lossy(body));
        }
        // A real rejection is still an Invalid verdict, not a non-verdict.
        let rejected = parse_validate_response(br#"{"valid":false}"#);
        assert_eq!(rejected.status, LicenseStatus::Invalid);
        assert!(rejected.is_verdict);
    }

    #[test]
    fn http_error_extracts_server_error_field() {
        let out = http_error_outcome(403, br#"{"error":"license suspended"}"#);
        assert_eq!(out.status, LicenseStatus::Invalid);
        assert_eq!(out.error_message.as_deref(), Some("license suspended"));
        assert!(out.is_verdict);
    }

    #[test]
    fn http_error_falls_back_to_generic_message() {
        let out = http_error_outcome(500, b"<html>oops</html>");
        assert_eq!(out.error_message.as_deref(), Some("Server error (HTTP 500)"));
        assert!(out.is_verdict);
    }

    #[test]
    fn empty_key_is_rejected() {
        let out = empty_key_outcome();
        assert_eq!(out.status, LicenseStatus::Invalid);
        assert_eq!(out.error_message.as_deref(), Some("License key cannot be empty"));
        assert!(!out.is_verdict);
    }
}
