use super::{
    HwProvider, HwProviderHealth, HwTranscriptionError, HttpRequest, HttpResponse, RetryDecision,
};

// ===========================================================================
// vocabulary
// ===========================================================================

/// Canonical vocabulary normalization for every egress path: sanitize each
/// term, drop the ones that sanitize to empty, de-duplicate case-insensitively
/// keeping first-seen casing and order, and stop at `limit`.
///
/// `limit` is `None` for "no cap"; `Some(0)` yields no terms, matching
/// `.Take(0)` / `.prefix(0)` on the hosts. Callers keep their own cap and
/// their own join separator — only this rule is shared.
#[uniffi::export]
pub fn normalize_vocabulary_terms(words: Vec<String>, limit: Option<u32>) -> Vec<String> {
    hw_net::helpers::keyword_boost_terms(&words, limit.map(|n| n as usize))
}
// retry
// ===========================================================================

/// Total transcription attempts before giving up.
#[uniffi::export]
pub fn retry_max_attempts() -> u32 {
    hw_net::retry::MAX_ATTEMPTS
}

/// Default **cumulative backoff** budget, in milliseconds, for one interactive
/// transcription attempt sequence. The default argument for the platform retry
/// drivers' `budgetMs` parameter; `0` means unbounded.
#[uniffi::export]
pub fn retry_default_budget_ms() -> u64 {
    hw_net::retry::DEFAULT_BUDGET_MS
}

/// Upper bound, in seconds, on a single honored `Retry-After` sleep.
#[uniffi::export]
pub fn retry_max_retry_after_secs() -> u64 {
    hw_net::retry::MAX_RETRY_AFTER_SECS
}

/// Map an HTTP status + response body to a `HwTranscriptionError`.
#[uniffi::export]
pub fn classify_error(status: u16, body: String) -> HwTranscriptionError {
    hw_net::retry::classify_error(status, &body).into()
}

/// Whether a classified error should be retried.
#[uniffi::export]
pub fn is_retryable(status: u16, body: String) -> bool {
    let err = hw_net::retry::classify_error(status, &body);
    hw_net::retry::is_retryable(&err)
}

/// Decide whether to retry, given the raw HTTP `status` + response `body`.
#[uniffi::export]
pub fn next_retry(
    attempt: u32,
    status: u16,
    body: String,
    retry_after: Option<u64>,
) -> RetryDecision {
    hw_net::retry::next_retry(attempt, status, &body, retry_after).into()
}

/// `next_retry` plus a **cumulative backoff budget** (issue #379).
///
/// `slept_ms` is the sum of the `delay_ms` values this call has already returned
/// for this attempt sequence — how much backoff the caller has been told to
/// sleep so far, starting at `0` on attempt 1. It is deliberately **not** the
/// sequence's wall clock: charging a slow request (a large upload) to the budget
/// would leave a big file with zero retries. A sleep that would push the running
/// total past `budget_ms` is refused, so a hard-down provider fails after 15s of
/// backoff instead of grinding through the full 1+2+4+8+16+32+64s series.
/// `budget_ms == 0` means unbounded, which makes this identical to `next_retry`.
/// Use `retry_default_budget_ms()` for interactive transcription.
#[uniffi::export]
pub fn next_retry_within_budget(
    attempt: u32,
    status: u16,
    body: String,
    retry_after: Option<u64>,
    slept_ms: u64,
    budget_ms: u64,
) -> RetryDecision {
    hw_net::retry::next_retry_within_budget(attempt, status, &body, retry_after, slept_ms, budget_ms)
        .into()
}

// ===========================================================================
// health
// ===========================================================================

/// Default HyperWhisper Cloud health URL for routed providers.
#[uniffi::export]
pub fn hw_cloud_health_default() -> String {
    hw_net::health::HW_CLOUD_HEALTH_DEFAULT.to_string()
}

/// Build a lightweight health-check request for `provider`.
#[uniffi::export]
pub fn build_health_request(provider: HwProvider, api_key: String) -> HttpRequest {
    hw_net::health::build_health_request(provider.into(), &api_key).into()
}

/// Like `build_health_request` but with an explicit HW Cloud base URL.
#[uniffi::export]
pub fn build_health_request_with_base(
    provider: HwProvider,
    api_key: String,
    base_url: Option<String>,
) -> HttpRequest {
    hw_net::health::build_health_request_with_base(provider.into(), &api_key, base_url.as_deref())
        .into()
}

/// Parse a health-check response into a verdict.
#[uniffi::export]
pub fn parse_health_response(provider: HwProvider, resp: HttpResponse) -> HwProviderHealth {
    hw_net::health::parse_health_response(provider.into(), &resp.into()).into()
}

// ===========================================================================
