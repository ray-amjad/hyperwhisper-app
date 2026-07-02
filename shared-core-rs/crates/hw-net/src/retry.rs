//! Cross-cutting error classification + retry policy (WP-B5).
//!
//! Unifies the divergent shipped platform policies onto **8 attempts**,
//! exponential backoff, honoring `Retry-After`, with no retry on terminal codes
//! (401/402/403/413/400/422). Plain Rust — no clock, no RNG: backoff is a pure
//! function of `(attempt, status, retry_after)`, so it is fully golden-testable.
//!
//! ## Parity sources
//!
//! - **Status → error** mirrors macOS `CloudWhisperProvider.swift`'s status switch
//!   and `providers::common::classify_http` (the per-provider parser already in
//!   this crate). This module's [`classify_error`] is the *body-only* variant used
//!   when the caller has the status + body text but not a full `HttpResponse`
//!   (e.g. the retry loop); it agrees with `classify_http` on every status.
//! - **Retry decision** unifies macOS `Retrying.swift` (the cloud transcription
//!   path uses `RetryConfiguration.transcription`: 10 attempts, retries 5xx +
//!   429 + transient network; never retries unauthorized/quota/fileTooLarge/
//!   invalidRequest per `TranscriptionError.isRetryable`) and Windows
//!   `OpenAIWhisperService.cs` (`MaxRetries = 3`, `Math.Pow(2, attempt)` second
//!   backoff, honors `Retry-After`, retries 429 + network errors).
//!
//!   The decision is driven off the **classified [`TranscriptionError`]**, not the
//!   raw HTTP status, so it cannot disagree with [`classify_error`]: a 429 whose
//!   body signals quota exhaustion classifies to `QuotaExceeded`, which is
//!   non-retryable (see [`is_retryable`]), exactly as macOS throws `.quotaExceeded`
//!   *before* the retry loop runs. [`next_retry`] (status-keyed) is kept as a thin
//!   wrapper that classifies first, so the two entry points share one source of
//!   truth.
//!
//! ## Unification choices (documented divergences)
//!
//! - **Attempt count → 8.** macOS uses 10 (transcription config) / Windows uses 3.
//!   Originally unified to 4; raised to **8 attempts** to restore the macOS
//!   transcription resilience without going all the way back to 10: attempts
//!   1..=7 may sleep-and-retry, and `attempt >= 8` returns
//!   [`RetryDecision::GiveUp`].
//! - **Backoff = `2^(attempt-1)` seconds → ms (1s, 2s, 4s).** We follow **macOS**
//!   (the verified platform). The macOS cloud *transcription* path
//!   (`CloudWhisperProvider.swift:76`) uses `RetryConfiguration.transcription`
//!   with `initialDelay = 1.0`, `backoffMultiplier = 2.0`, so its series is
//!   `1.0 * 2^(attempt-1)` = 1s, 2s, 4s, 8s. (The unused `RetryConfiguration.cloud`
//!   with `initialDelay = 2.0` would give 2s/4s/8s, and Windows `Math.Pow(2,
//!   attempt)` also gives 2s/4s/8s — but neither is the config the verified macOS
//!   transcription loop actually runs, so per the prefer-macOS rule we adopt the
//!   `.transcription` series.) Expressed in milliseconds, with **no jitter** (RNG
//!   is forbidden in the core; jitter is a platform concern and was small — macOS
//!   `0...0.3` of base for `.transcription`). Documented divergence from Windows's
//!   2s/4s/8s.
//! - **5xx is retryable.** macOS retries `serverError`; Windows does not catch its
//!   `ServerError` and so does not retry 5xx. We follow **macOS** (the verified
//!   platform): retry on 5xx. Documented divergence from Windows.
//! - **`Retry-After` honored + clamped to 10s per sleep.** Matches the clamp
//!   constant macOS uses for honored `Retry-After` sleeps
//!   (`RetryConfiguration.maxPollRetryAfterSeconds = 10`). Note the macOS
//!   transcription retry loop itself does not honor `Retry-After` (it always uses
//!   `config.delay`); the 10s clamp originates in the macOS *polling* loops
//!   (Soniox/AssemblyAI). We adopt honoring-with-clamp as a deliberate unification:
//!   when `retry_after` is present it overrides the exponential delay, clamped to
//!   10s (10_000 ms).

use crate::contract::{RetryDecision, TranscriptionError};

/// Total transcription attempts before giving up (unified across platforms).
/// Attempts 1..=7 may retry; the 8th (and beyond) is terminal.
///
/// Raised from 4 → 8 to restore the transcription retry resilience the verified
/// macOS path had pre-unification (it used 10). 8 is a deliberate balance: more
/// resilient than the unified 4, less aggressive than 10 for the providers that
/// previously used only 3. Exponential backoff (capped by an honored `Retry-After`
/// at [`MAX_RETRY_AFTER_SECS`] per sleep) bounds the total wall-clock wait; the
/// worst case with no `Retry-After` is the exponential series 1+2+4+8+16+32+64s
/// ≈ 127s across the 7 retryable attempts.
pub const MAX_ATTEMPTS: u32 = 8;

/// Upper bound, in seconds, on a single honored `Retry-After` sleep. Mirrors
/// macOS `RetryConfiguration.maxPollRetryAfterSeconds`. A hostile/misconfigured
/// server can return a huge `Retry-After`; clamping keeps the loop bounded.
pub const MAX_RETRY_AFTER_SECS: u64 = 10;

/// Map an HTTP status + response body to a [`TranscriptionError`].
///
/// PARITY (matches `providers::common::classify_http` and macOS
/// `CloudWhisperProvider.swift`):
/// - 401 / 403 → `Unauthorized`
/// - 402 → `QuotaExceeded`
/// - 413 → `FileTooLarge`
/// - 429 → `QuotaExceeded` when the body signals quota exhaustion
///   (`error.code`/`error.type` == `insufficient_quota`, or a message mentioning
///   `quota` / `billing` / `insufficient_quota`), else
///   `RateLimited { retry_after_secs }` parsed from the body when present.
/// - 408 → `ProviderUnavailable { status }` (request timeout is transient, same
///   as 5xx; agrees with `classify_http` — see module doc)
/// - 5xx → `ProviderUnavailable { status }`
/// - any other status (other 4xx, and any non-2xx not above) → `BadRequest`
///
/// `body` is the raw response body text. `Retry-After` normally lives in a header,
/// but this body-only entry point also parses a `retry_after` / `retryAfter`
/// field from the JSON body when present (the task contract: "retry_after parsed
/// from body if present"); the header-based path lives in `classify_http`.
pub fn classify_error(status: u16, body: &str) -> TranscriptionError {
    let json: Option<serde_json::Value> = serde_json::from_str(body).ok();

    match status {
        401 | 403 => TranscriptionError::Unauthorized,
        402 => TranscriptionError::QuotaExceeded,
        408 => TranscriptionError::ProviderUnavailable { status },
        413 => TranscriptionError::FileTooLarge,
        429 => {
            if is_quota_error(json.as_ref()) {
                TranscriptionError::QuotaExceeded
            } else {
                TranscriptionError::RateLimited {
                    retry_after_secs: retry_after_from_body(json.as_ref()),
                }
            }
        }
        500..=599 => TranscriptionError::ProviderUnavailable { status },
        _ => TranscriptionError::BadRequest {
            status,
            message: error_message(json.as_ref(), body),
        },
    }
}

/// Whether a classified [`TranscriptionError`] should be retried.
///
/// PARITY — mirrors macOS `TranscriptionError.isRetryable`
/// (`TranscriptionError.swift:113-121`):
/// - **Retryable:** `RateLimited` (macOS `.rateLimited`) and `ProviderUnavailable`
///   (macOS `.serverError` / `.providerNotAvailable` / transient network / timeout).
/// - **Terminal:** `Unauthorized`, `QuotaExceeded`, `FileTooLarge`, `BadRequest`
///   (macOS `.unauthorized` / `.quotaExceeded` / `.insufficientCredits` /
///   `.audioFileTooLarge` / `.invalidRequest`), and `NoSpeech` (macOS
///   `.noSpeechDetected`) / `Parse` (a malformed response is not made valid by
///   retrying).
///
/// This is the single source of truth for the retry decision. Crucially, a 429
/// whose body signals quota exhaustion classifies to `QuotaExceeded` (see
/// [`classify_error`]), which is **terminal** here — matching macOS, which throws
/// `.quotaExceeded` *before* the retry loop ever sees the 429. Keying off the raw
/// status would (wrongly) retry such a 429.
pub fn is_retryable(err: &TranscriptionError) -> bool {
    match err {
        TranscriptionError::RateLimited { .. } | TranscriptionError::ProviderUnavailable { .. } => {
            true
        }
        TranscriptionError::Unauthorized
        | TranscriptionError::QuotaExceeded
        | TranscriptionError::FileTooLarge
        | TranscriptionError::BadRequest { .. }
        | TranscriptionError::NoSpeech
        | TranscriptionError::Parse { .. } => false,
    }
}

/// Decide whether to retry the attempt that just failed, keyed off the
/// **classified error** (the authoritative entry point).
///
/// `attempt` is 1-based (the attempt that just failed). `err` is the
/// [`TranscriptionError`] the response classified to (via [`classify_error`]);
/// `retry_after` is the `Retry-After` value in seconds when the platform has one
/// (from header or body).
///
/// PARITY / unification:
/// - **No retry** when `!is_retryable(err)` → `GiveUp`. This covers the terminal
///   set (`Unauthorized` / `QuotaExceeded` / `FileTooLarge` / `BadRequest` /
///   `NoSpeech` / `Parse`), matching `TranscriptionError.isRetryable == false`.
///   In particular a 429-quota body → `QuotaExceeded` → `GiveUp`.
/// - **Retry** on `RateLimited` (429) and `ProviderUnavailable` (5xx). 5xx follows
///   macOS (Windows does not retry 5xx — documented divergence above).
/// - **`GiveUp` once `attempt >= MAX_ATTEMPTS` (8)** regardless of error — the
///   attempts are exhausted.
/// - Delay = `2^(attempt-1)` seconds in ms (macOS `.transcription` series: 1s, 2s,
///   4s), unless `retry_after` is present, in which case that value is honored and
///   **clamped to [`MAX_RETRY_AFTER_SECS`] (10s)** per sleep.
pub fn next_retry_for_error(
    attempt: u32,
    err: &TranscriptionError,
    retry_after: Option<u64>,
) -> RetryDecision {
    // Attempts exhausted: terminal regardless of error.
    if attempt >= MAX_ATTEMPTS {
        return RetryDecision::GiveUp;
    }

    if !is_retryable(err) {
        return RetryDecision::GiveUp;
    }

    RetryDecision::Retry {
        delay_ms: backoff_ms(attempt, retry_after),
    }
}

/// Decide whether to retry, given the raw HTTP `status` + response `body`.
///
/// Thin status-keyed convenience over [`next_retry_for_error`]: it [`classify_error`]s
/// the `(status, body)` first, so it shares one source of truth and therefore
/// cannot disagree with classification. A 429 with an `insufficient_quota` body
/// classifies to `QuotaExceeded` and yields `GiveUp` — unlike a naive
/// `matches!(status, 429 | 5xx)` check, which would wrongly retry quota
/// exhaustion that the verified macOS platform treats as terminal.
///
/// `attempt` is 1-based (the attempt that just failed); `retry_after` is the
/// `Retry-After` value in seconds when the platform has one (header or body).
pub fn next_retry(
    attempt: u32,
    status: u16,
    body: &str,
    retry_after: Option<u64>,
) -> RetryDecision {
    let err = classify_error(status, body);
    next_retry_for_error(attempt, &err, retry_after)
}

/// Compute the sleep before retrying after `attempt` (1-based).
///
/// - With `retry_after`: honor it, clamped to [`MAX_RETRY_AFTER_SECS`] seconds.
/// - Without: exponential `2^(attempt-1)` seconds → milliseconds, matching macOS
///   `RetryConfiguration.transcription` (`initialDelay = 1.0`,
///   `backoffMultiplier = 2.0`): 1s, 2s, 4s for attempts 1, 2, 3.
fn backoff_ms(attempt: u32, retry_after: Option<u64>) -> u64 {
    if let Some(secs) = retry_after {
        return secs.min(MAX_RETRY_AFTER_SECS) * 1_000;
    }
    // 2^(attempt-1) seconds → ms. attempt is 1-based: 1s, 2s, 4s, ... up to 64s
    // for attempt 7. `1u64 << (attempt-1)` == 2^(attempt-1); attempt >= 1 and
    // attempt < MAX_ATTEMPTS (8) here so the shift is at most `1u64 << 6` and
    // cannot underflow or overflow.
    (1u64 << (attempt - 1)) * 1_000
}

/// True when a 429 body indicates permanent quota exhaustion (vs. transient rate
/// limiting). Mirrors `is_quota_error` in `providers::common` and the macOS
/// `isQuotaError` check EXACTLY: only the nested `error.code` / `error.type` ==
/// `insufficient_quota`, or `error.message` mentioning `quota` / `billing`. No
/// top-level `message` fallback (that would diverge from the result-parse path
/// and macOS — see body comment).
fn is_quota_error(json: Option<&serde_json::Value>) -> bool {
    // Inspect ONLY the nested OpenAI-style `error` object — byte-for-byte
    // identical to `providers::common::is_quota_error` and macOS
    // `CloudWhisperProvider.swift` (which checks `errorData["error"]["code"/
    // "type"/"message"]`). Deliberately NO top-level `message` fallback: adding
    // one here would make the retry path classify a `{"message":"...billing..."}`
    // 429 as terminal QuotaExceeded while the result-parse path + macOS treat it
    // as retryable RateLimited — an internal inconsistency. The two in-crate
    // classifiers must agree on every status (see module doc).
    let Some(error) = json.and_then(|j| j.get("error")) else {
        return false;
    };
    let code = error.get("code").and_then(|v| v.as_str()).unwrap_or("");
    let kind = error.get("type").and_then(|v| v.as_str()).unwrap_or("");
    let msg = error
        .get("message")
        .and_then(|v| v.as_str())
        .unwrap_or("")
        .to_lowercase();
    code == "insufficient_quota"
        || kind == "insufficient_quota"
        || msg.contains("quota")
        || msg.contains("billing")
}

/// Parse a `Retry-After`-equivalent value from a JSON body, if present. Accepts a
/// top-level `retry_after` or `retryAfter` field, as a number or numeric string.
/// (The header-based path lives in `providers::common::classify_http`.)
fn retry_after_from_body(json: Option<&serde_json::Value>) -> Option<u64> {
    let j = json?;
    for key in ["retry_after", "retryAfter"] {
        if let Some(v) = j.get(key) {
            if let Some(n) = v.as_u64() {
                return Some(n);
            }
            if let Some(s) = v.as_str() {
                if let Ok(n) = s.trim().parse::<u64>() {
                    return Some(n);
                }
            }
        }
    }
    None
}

/// Best-effort error message: `error.message`, then top-level `message` / `error`
/// (string), then the first 200 chars of the raw body. Matches
/// `providers::common::error_message`.
fn error_message(json: Option<&serde_json::Value>, raw: &str) -> String {
    if let Some(j) = json {
        if let Some(m) = j
            .get("error")
            .and_then(|e| e.get("message"))
            .and_then(|v| v.as_str())
        {
            return m.to_string();
        }
        if let Some(m) = j.get("message").and_then(|v| v.as_str()) {
            return m.to_string();
        }
        if let Some(m) = j.get("error").and_then(|v| v.as_str()) {
            return m.to_string();
        }
    }
    raw.chars().take(200).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    // ---- classify_error: status → error table (golden) ----

    #[test]
    fn classify_401_403_unauthorized() {
        assert_eq!(classify_error(401, ""), TranscriptionError::Unauthorized);
        assert_eq!(classify_error(403, "{}"), TranscriptionError::Unauthorized);
    }

    #[test]
    fn classify_402_quota_exceeded() {
        assert_eq!(classify_error(402, ""), TranscriptionError::QuotaExceeded);
    }

    #[test]
    fn classify_413_file_too_large() {
        assert_eq!(classify_error(413, ""), TranscriptionError::FileTooLarge);
    }

    #[test]
    fn classify_429_rate_limited_when_not_quota() {
        assert_eq!(
            classify_error(429, r#"{"error":{"message":"slow down"}}"#),
            TranscriptionError::RateLimited {
                retry_after_secs: None
            }
        );
    }

    #[test]
    fn classify_429_quota_via_insufficient_quota_code() {
        assert_eq!(
            classify_error(
                429,
                r#"{"error":{"code":"insufficient_quota","message":"You exceeded your quota"}}"#
            ),
            TranscriptionError::QuotaExceeded
        );
    }

    #[test]
    fn classify_429_quota_via_type_field() {
        assert_eq!(
            classify_error(429, r#"{"error":{"type":"insufficient_quota"}}"#),
            TranscriptionError::QuotaExceeded
        );
    }

    #[test]
    fn classify_429_quota_via_message_keywords() {
        assert_eq!(
            classify_error(429, r#"{"error":{"message":"check your billing"}}"#),
            TranscriptionError::QuotaExceeded
        );
        assert_eq!(
            classify_error(429, r#"{"error":{"message":"monthly quota reached"}}"#),
            TranscriptionError::QuotaExceeded
        );
    }

    #[test]
    fn classify_429_rate_limited_parses_retry_after_from_body() {
        assert_eq!(
            classify_error(429, r#"{"retry_after":15}"#),
            TranscriptionError::RateLimited {
                retry_after_secs: Some(15)
            }
        );
        // camelCase + string numeric form.
        assert_eq!(
            classify_error(429, r#"{"retryAfter":"8"}"#),
            TranscriptionError::RateLimited {
                retry_after_secs: Some(8)
            }
        );
    }

    #[test]
    fn classify_408_provider_unavailable() {
        // Request timeout is transient — retryable ProviderUnavailable, not the
        // terminal BadRequest fallthrough.
        assert_eq!(
            classify_error(408, ""),
            TranscriptionError::ProviderUnavailable { status: 408 }
        );
        assert_eq!(
            classify_error(408, r#"{"message":"Request Timeout"}"#),
            TranscriptionError::ProviderUnavailable { status: 408 }
        );
    }

    #[test]
    fn classify_5xx_provider_unavailable() {
        assert_eq!(
            classify_error(500, ""),
            TranscriptionError::ProviderUnavailable { status: 500 }
        );
        assert_eq!(
            classify_error(503, "{}"),
            TranscriptionError::ProviderUnavailable { status: 503 }
        );
        assert_eq!(
            classify_error(599, ""),
            TranscriptionError::ProviderUnavailable { status: 599 }
        );
    }

    #[test]
    fn classify_400_422_bad_request_with_message() {
        assert_eq!(
            classify_error(400, r#"{"error":{"message":"bad model"}}"#),
            TranscriptionError::BadRequest {
                status: 400,
                message: "bad model".to_string()
            }
        );
        assert_eq!(
            classify_error(422, r#"{"message":"unprocessable"}"#),
            TranscriptionError::BadRequest {
                status: 422,
                message: "unprocessable".to_string()
            }
        );
    }

    #[test]
    fn classify_bad_request_falls_back_to_raw_body() {
        // Non-JSON body → first 200 chars used as the message.
        assert_eq!(
            classify_error(404, "not found"),
            TranscriptionError::BadRequest {
                status: 404,
                message: "not found".to_string()
            }
        );
    }

    // ---- next_retry: decisions across attempts 1..5 ----
    // `next_retry(attempt, status, body, retry_after)` — body is "" for the
    // status-only cases (a bare 429 is rate-limited, not quota).

    #[test]
    fn retry_429_backoff_across_attempts() {
        // Exponential 2^(attempt-1) seconds → ms (macOS .transcription series:
        // 1s, 2s, 4s for attempts 1, 2, 3).
        assert_eq!(
            next_retry(1, 429, "", None),
            RetryDecision::Retry { delay_ms: 1_000 }
        );
        assert_eq!(
            next_retry(2, 429, "", None),
            RetryDecision::Retry { delay_ms: 2_000 }
        );
        assert_eq!(
            next_retry(3, 429, "", None),
            RetryDecision::Retry { delay_ms: 4_000 }
        );
        // Attempts 4..=7 keep retrying with exponential backoff (8-attempt budget).
        assert_eq!(
            next_retry(4, 429, "", None),
            RetryDecision::Retry { delay_ms: 8_000 }
        );
        assert_eq!(
            next_retry(7, 429, "", None),
            RetryDecision::Retry { delay_ms: 64_000 }
        );
        // 8th attempt failed → attempts exhausted.
        assert_eq!(next_retry(8, 429, "", None), RetryDecision::GiveUp);
        assert_eq!(next_retry(9, 429, "", None), RetryDecision::GiveUp);
    }

    #[test]
    fn retry_5xx_is_retryable_until_exhausted() {
        // macOS parity: 5xx retries (Windows would not — documented divergence).
        assert_eq!(
            next_retry(1, 500, "", None),
            RetryDecision::Retry { delay_ms: 1_000 }
        );
        assert_eq!(
            next_retry(3, 503, "", None),
            RetryDecision::Retry { delay_ms: 4_000 }
        );
        // Still retryable on the 4th attempt now (8-attempt budget); terminal at 8.
        assert_eq!(
            next_retry(4, 503, "", None),
            RetryDecision::Retry { delay_ms: 8_000 }
        );
        assert_eq!(next_retry(8, 503, "", None), RetryDecision::GiveUp);
    }

    #[test]
    fn retry_408_is_retryable_until_exhausted() {
        // 408 request timeout retries like a 5xx.
        assert_eq!(
            next_retry(1, 408, "", None),
            RetryDecision::Retry { delay_ms: 1_000 }
        );
        assert_eq!(
            next_retry(3, 408, "", None),
            RetryDecision::Retry { delay_ms: 4_000 }
        );
        assert_eq!(next_retry(8, 408, "", None), RetryDecision::GiveUp);
    }

    #[test]
    fn no_retry_on_terminal_codes() {
        for status in [400u16, 401, 402, 403, 413, 422] {
            for attempt in 1..=5 {
                assert_eq!(
                    next_retry(attempt, status, "", None),
                    RetryDecision::GiveUp,
                    "status={status} attempt={attempt}"
                );
            }
        }
    }

    #[test]
    fn unknown_non_retryable_status_gives_up() {
        // A 404/418 etc. classifies to BadRequest → terminal.
        assert_eq!(next_retry(1, 404, "", None), RetryDecision::GiveUp);
        assert_eq!(next_retry(1, 418, "", None), RetryDecision::GiveUp);
    }

    // ---- 429-quota is TERMINAL (the parity bug this WP fixed) ----

    #[test]
    fn classify_429_quota_then_give_up() {
        // GOLDEN: a 429 whose body signals insufficient_quota classifies to
        // QuotaExceeded AND is terminal — matching macOS, which throws
        // .quotaExceeded BEFORE the retry loop. A raw-status check would retry.
        let body = r#"{"error":{"code":"insufficient_quota","message":"You exceeded your quota"}}"#;
        assert_eq!(classify_error(429, body), TranscriptionError::QuotaExceeded);
        assert!(!is_retryable(&TranscriptionError::QuotaExceeded));
        // Through both entry points, on an attempt that would otherwise retry.
        assert_eq!(next_retry(1, 429, body, None), RetryDecision::GiveUp);
        assert_eq!(
            next_retry_for_error(1, &TranscriptionError::QuotaExceeded, None),
            RetryDecision::GiveUp
        );
        // Even a Retry-After cannot resurrect a quota-terminal 429.
        assert_eq!(next_retry(1, 429, body, Some(2)), RetryDecision::GiveUp);
    }

    #[test]
    fn rate_limited_429_still_retries() {
        // A non-quota 429 stays retryable (regression guard for the quota fix).
        let body = r#"{"error":{"message":"slow down"}}"#;
        assert_eq!(
            classify_error(429, body),
            TranscriptionError::RateLimited {
                retry_after_secs: None
            }
        );
        assert_eq!(
            next_retry(1, 429, body, None),
            RetryDecision::Retry { delay_ms: 1_000 }
        );
    }

    // ---- classify_error AGREES WITH classify_http on every status ----

    /// Build a minimal `HttpResponse` for the body-only `classify_http` path.
    fn resp(status: u16, body: &str) -> crate::contract::HttpResponse {
        crate::contract::HttpResponse {
            status,
            headers: Vec::new(),
            body: body.as_bytes().to_vec(),
        }
    }

    #[test]
    fn classify_error_agrees_with_classify_http_on_toplevel_message_429() {
        // GOLDEN (the divergence this fix closed): a 429 with a top-level
        // `{"message":"...billing..."}` body (NOT nested under `error`) must NOT
        // be treated as quota by either classifier. `classify_http` (result-parse
        // path) and macOS only inspect the nested `error` object, so this is a
        // retryable RateLimited — and `classify_error` (retry path) must match.
        for body in [
            r#"{"message":"rate limit exceeded, check your billing"}"#,
            r#"{"message":"monthly quota reached"}"#,
            r#"{"message":"insufficient_quota"}"#,
        ] {
            let via_retry = classify_error(429, body);
            let via_parse = crate::providers::common::classify_http(&resp(429, body), body);
            assert_eq!(
                via_retry, via_parse,
                "classify_error vs classify_http disagree on body={body}"
            );
            assert_eq!(
                via_retry,
                TranscriptionError::RateLimited {
                    retry_after_secs: None
                },
                "top-level message must stay RateLimited, body={body}"
            );
            // And therefore the retry path stays retryable, not terminal.
            assert!(is_retryable(&via_retry), "body={body}");
        }
    }

    #[test]
    fn classify_error_agrees_with_classify_http_on_nested_quota_429() {
        // Both classifiers DO agree the nested-error quota shape is terminal.
        let body = r#"{"error":{"code":"insufficient_quota","message":"You exceeded your quota"}}"#;
        let via_retry = classify_error(429, body);
        let via_parse = crate::providers::common::classify_http(&resp(429, body), body);
        assert_eq!(via_retry, via_parse);
        assert_eq!(via_retry, TranscriptionError::QuotaExceeded);
    }

    // ---- is_retryable: full TranscriptionError parity table ----

    #[test]
    fn is_retryable_mirrors_macos_table() {
        // Retryable (macOS .rateLimited / .serverError / .providerNotAvailable).
        assert!(is_retryable(&TranscriptionError::RateLimited {
            retry_after_secs: None
        }));
        assert!(is_retryable(&TranscriptionError::ProviderUnavailable {
            status: 503
        }));
        // Terminal (macOS isRetryable == false).
        assert!(!is_retryable(&TranscriptionError::Unauthorized));
        assert!(!is_retryable(&TranscriptionError::QuotaExceeded));
        assert!(!is_retryable(&TranscriptionError::FileTooLarge));
        assert!(!is_retryable(&TranscriptionError::BadRequest {
            status: 400,
            message: String::new()
        }));
        assert!(!is_retryable(&TranscriptionError::NoSpeech));
        assert!(!is_retryable(&TranscriptionError::Parse {
            message: String::new()
        }));
    }

    // ---- next_retry_for_error: error-keyed entry point ----

    #[test]
    fn next_retry_for_error_decisions() {
        let rl = TranscriptionError::RateLimited {
            retry_after_secs: None,
        };
        assert_eq!(
            next_retry_for_error(1, &rl, None),
            RetryDecision::Retry { delay_ms: 1_000 }
        );
        // 8-attempt budget: attempt 4 still retries; attempt 8 is exhausted.
        assert_eq!(
            next_retry_for_error(4, &rl, None),
            RetryDecision::Retry { delay_ms: 8_000 }
        );
        assert_eq!(next_retry_for_error(8, &rl, None), RetryDecision::GiveUp);
        assert_eq!(
            next_retry_for_error(1, &TranscriptionError::Unauthorized, None),
            RetryDecision::GiveUp
        );
    }

    // ---- Retry-After honoring + clamp ----

    #[test]
    fn retry_after_is_honored_over_exponential() {
        // 5s Retry-After on attempt 1 (exp would be 1s) → 5s wins.
        assert_eq!(
            next_retry(1, 429, "", Some(5)),
            RetryDecision::Retry { delay_ms: 5_000 }
        );
        // 3s Retry-After on attempt 3 (exp would be 4s) → 3s wins (honored, not max).
        assert_eq!(
            next_retry(3, 429, "", Some(3)),
            RetryDecision::Retry { delay_ms: 3_000 }
        );
    }

    #[test]
    fn retry_after_is_clamped_to_10s() {
        // macOS maxPollRetryAfterSeconds = 10: a hostile 300s clamps to 10s.
        assert_eq!(
            next_retry(1, 429, "", Some(300)),
            RetryDecision::Retry {
                delay_ms: MAX_RETRY_AFTER_SECS * 1_000
            }
        );
        assert_eq!(
            next_retry(2, 503, "", Some(60)),
            RetryDecision::Retry { delay_ms: 10_000 }
        );
        // Exactly 10s is unchanged.
        assert_eq!(
            next_retry(1, 429, "", Some(10)),
            RetryDecision::Retry { delay_ms: 10_000 }
        );
    }

    #[test]
    fn retry_after_ignored_when_attempts_exhausted() {
        // Even a valid Retry-After can't resurrect an exhausted budget.
        assert_eq!(next_retry(8, 429, "", Some(2)), RetryDecision::GiveUp);
    }

    #[test]
    fn retry_after_ignored_on_terminal_status() {
        // 401 with a Retry-After is still terminal.
        assert_eq!(next_retry(1, 401, "", Some(2)), RetryDecision::GiveUp);
    }
}
