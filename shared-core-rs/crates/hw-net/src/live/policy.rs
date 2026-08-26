//! Terminal vs transient classification for a live streaming session.
//!
//! Three classifiers, three different doors a failure comes through: an error
//! frame on an open socket ([`classify_error_message`]), an HTTP status where a
//! `101 Switching Protocols` should have been ([`upgrade_refusal`]), and a close
//! code ([`is_terminal_close_code`]).
//!
//! Ported from macOS `StreamingProviderErrorPolicy.swift`, which was written to
//! fix three linked Sentry issues, plus the RFC-6455 close-code default from
//! Windows `IStreamingProviderStrategy.cs`.

/// The outcome of classifying a provider error message.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LiveErrorOutcome {
    /// Reconnecting cannot help — the account, key, quota or permission is the
    /// problem. The client should treat the provider's follow-up socket close as
    /// expected and surface the message as it stands.
    Terminal,
    /// The failure may clear on its own; leave the reconnect path alone.
    Transient,
}

/// Why a server refused the websocket upgrade outright, when the refusal is one
/// the user has to act on.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LiveUpgradeRefusal {
    /// HTTP 402. The account has no balance to open a session with.
    InsufficientCredits,
    /// HTTP 401 / 403. The key is missing, wrong, revoked or not permitted.
    Unauthorized,
}

/// Lowercased substrings that mean the session cannot be rescued by
/// reconnecting with the same credentials.
///
/// Word forms only, and each one names a state the *user* has to change (top up,
/// fix the key, reactivate the account). Nothing here matches on digits — see
/// the note on [`classify_error_message`]. Both the underscore and the spaced
/// spelling are listed where providers are known to use both.
///
/// The first entry is the flagship case: HyperWhisper Cloud — the default
/// provider — sends exactly `{"type":"error","message":"Credit balance
/// exhausted"}` when a session outruns the account's balance. Without it the
/// default path produced the whole fan-out this policy exists to stop.
pub const TERMINAL_ERROR_MARKERS: [&str; 20] = [
    "credit balance exhausted",
    "no credits remaining",
    "insufficient credits",
    "insufficient_quota",
    "insufficient quota",
    "exceeded your current quota",
    "quota exceeded",
    "invalid api key",
    "incorrect api key",
    "invalid_api_key",
    "api key not valid",
    "unauthorized",
    "authentication_error",
    "authentication failed",
    "forbidden",
    "permission_denied",
    "permission denied",
    "billing",
    "payment required",
    "account is not active",
];

/// Classifies the `message` payload of a provider error frame as either a
/// terminal provider condition or a transient one.
///
/// ## Why this exists
///
/// A terminal provider fault — a BYO-key account with no credits left, a revoked
/// key — arrives as an error frame, and the provider then closes the socket
/// itself. macOS's client fired its error callback (full flow teardown) for that
/// frame but never marked the close as its own, so the provider's close a moment
/// later read as an unexpected disconnect (**HYPERWHISPER-MH**) and started an
/// auto-reconnect that could only fail the same way, reported as a second,
/// generic failure (**HYPERWHISPER-MG**) whose "connection lost and reconnect
/// failed" toast overwrote the actionable message the first error had already
/// put on screen (**HYPERWHISPER-RW**). One provider fault, three Sentry issues
/// and a worse message for the user. Windows and Linux never had the fix at all.
///
/// Classifying the frame lets the client mark the impending close as expected
/// and skip a reconnect that was never going to succeed. It changes what the
/// client *retries* and what it *reports* — never what the user is told: the
/// actionable message still reaches the error callback unchanged.
///
/// ## Why string matching
///
/// The normalized error event carries a message and nothing else. OpenAI's
/// realtime error frame does have a machine-readable `error.code`
/// (`insufficient_quota`) and `error.type`, but every strategy's decoder keeps
/// only `message`, and widening the event would touch the enum plus every
/// strategy that emits it. The text is the only signal available here.
///
/// ## The rate-limit asymmetry is deliberate
///
/// A quota marker (`insufficient_quota`, "exceeded your current quota") is
/// terminal, while a plain "rate limit reached for requests" stays transient —
/// even though OpenAI returns the former under a `rate_limit_error` *type*.
/// Classification reads the message text, not the type, so the two separate
/// cleanly, and they should: an exhausted quota stays exhausted until the user
/// pays, whereas a rate limit clears on its own and a reconnect a second later
/// is exactly the right response. This also keeps the streaming path agreeing
/// with the batch pipeline's error classification, where `rate_limited` is
/// retryable and `insufficient_credits` / `quota_exceeded` are not.
///
/// ## No bare digit markers
///
/// Deliberately no `"401"` / `"403"` substring test — provider payloads embed
/// request ids such as `req_4013…`, so a digit match would brand unrelated blips
/// terminal and silently stop retrying them. Only word forms are matched.
///
/// Unrecognised wording — including an empty message — falls through to
/// [`LiveErrorOutcome::Transient`] on purpose, so a payload nobody has seen yet
/// keeps today's behaviour instead of quietly losing its reconnect.
pub fn classify_error_message(message: &str) -> LiveErrorOutcome {
    let normalized = message.to_lowercase();
    if TERMINAL_ERROR_MARKERS
        .iter()
        .any(|marker| normalized.contains(marker))
    {
        LiveErrorOutcome::Terminal
    } else {
        LiveErrorOutcome::Transient
    }
}

/// Classifies the HTTP status of a websocket upgrade that never reached 101.
///
/// ## Why this exists
///
/// [`classify_error_message`] only covers a user who runs out of credits
/// *during* a session. The same user one keypress later hits a different path
/// entirely: HyperWhisper Cloud requires 30 seconds of balance to open a
/// streaming session at all, and refuses the upgrade with 402 before any socket
/// exists. The receive call then fails with a plain transport error carrying
/// none of that: the client read it as an unexpected disconnect, reported it,
/// retried three times into the same 402, reported the exhausted retries, and
/// told the user "Connection lost after multiple retries" — five Sentry events
/// and a message naming the wrong problem, repeated on every attempt until the
/// user topped up. The status was on the response all along; it was simply never
/// read.
///
/// ## Why only these three statuses
///
/// Each names a state the *user* changes (top up, fix the key), which is the
/// same bar [`TERMINAL_ERROR_MARKERS`] holds to. Everything else — 429, 5xx, a
/// proxy mangling the upgrade — keeps today's reconnect, because a retry a
/// second later is a reasonable answer to it. `None` for an unrecognised status
/// is therefore the safe default, not a gap.
///
/// 101 is the status a socket that actually opened carries, so every mid-session
/// drop reads its response and finds nothing here.
pub fn upgrade_refusal(status: u16) -> Option<LiveUpgradeRefusal> {
    match status {
        402 => Some(LiveUpgradeRefusal::InsufficientCredits),
        401 | 403 => Some(LiveUpgradeRefusal::Unauthorized),
        _ => None,
    }
}

/// Whether a websocket close code means the session cannot recover and the
/// connection should end immediately instead of going through the client's
/// reconnect/backoff path.
///
/// This is the WebSocket protocol's own standard non-recoverable set (RFC 6455
/// §7.4.1), which applies to any provider regardless of its wire protocol: 1002
/// (Protocol Error), 1003 (Unsupported Data), 1007 (Invalid Payload Data), 1008
/// (Policy Violation), 1009 (Message Too Big) and 1011 (Internal Error).
///
/// The standard transient codes are excluded on purpose so they keep falling
/// through to the reconnect path: 1000 (Normal — the caller handles it
/// separately), 1001 (Going Away), 1006 (Abnormal / no close frame), 1012
/// (Service Restart) and 1013 (Try Again Later).
///
/// A provider that uses close codes of its own to signal an unrecoverable
/// session combines them *with* this set rather than replacing it — macOS does
/// exactly that for HyperWhisper Cloud's 4001/4002 in its client.
pub fn is_terminal_close_code(code: u16) -> bool {
    matches!(code, 1002 | 1003 | 1007 | 1008 | 1009 | 1011)
}
