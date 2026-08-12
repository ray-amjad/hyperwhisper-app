//
//  StreamingProviderErrorPolicy.swift
//  hyperwhisper
//
//  Decides whether the message carried by a streaming provider's error frame
//  describes a terminal condition (reconnecting cannot rescue the session) or a
//  transient one (the normal auto-reconnect path still applies).
//
//  A free namespace rather than a member of `StreamingTranscriptionClient`:
//  that type is `@MainActor` and owns a live `URLSession` plus a provider
//  strategy, which would make this untestable.
//

import Foundation

/// Classifies the `message` payload of a `StreamingProviderEvent.error` as
/// either a terminal provider condition or a transient one.
///
/// ## Why this exists
///
/// A terminal provider fault — a BYO-key account with no credits left, a
/// revoked key — arrives as an error frame, and the provider then closes the
/// socket itself. `StreamingTranscriptionClient` fired `onError` (full flow
/// teardown) for that frame but never set `didInitiateClose`, so the provider's
/// own close a moment later read as an unexpected disconnect
/// (**HYPERWHISPER-MH**) and started an auto-reconnect that could only fail the
/// same way, reported as a second, generic failure (**HYPERWHISPER-MG**) whose
/// "connection lost and reconnect failed" toast overwrote the actionable
/// message the first error had already put on screen (**HYPERWHISPER-RW**). One
/// provider fault, three Sentry issues and a worse message for the user.
///
/// Classifying the frame lets the client mark the impending close as expected
/// and skip a reconnect that was never going to succeed. It changes what the
/// client *retries* and what it *reports* — never what the user is told: the
/// actionable message still reaches `onError` and the error toast unchanged.
///
/// ## Why string matching
///
/// The normalized `StreamingProviderEvent.error` carries a message and nothing
/// else. OpenAI's realtime error frame does have a machine-readable
/// `error.code` (`insufficient_quota`) and `error.type`, but the strategy's
/// decoder keeps only `message`, and widening the event would touch the enum
/// plus every strategy that emits it. The text is the only signal available
/// here. `lowercased().contains(...)` is the house idiom for this
/// (`TranscriptionPipeline.isTransientProviderAvailabilityReason`).
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
/// with `TranscriptionPipeline+ErrorClassification`, where `rate_limited` is
/// retryable and `insufficient_credits` / `quota_exceeded` are not.
///
/// - Note: Deliberately no bare `"401"` / `"403"` substring test — provider
///   payloads embed request ids such as `req_4013…`, so a digit match would
///   brand unrelated blips terminal and silently stop retrying them. Only word
///   forms are matched; `requestIdThatContainsFourZeroOneStaysTransient` pins
///   that down.
enum StreamingProviderErrorPolicy {

    /// The outcome of classifying a provider error message.
    enum Outcome: Equatable {
        /// Reconnecting cannot help — the account, key, quota or permission is
        /// the problem. The client should treat the provider's follow-up socket
        /// close as expected and surface the message as it stands.
        case terminal
        /// The failure may clear on its own; leave the reconnect path alone.
        case transient
    }

    /// Lowercased substrings that mean the session cannot be rescued by
    /// reconnecting with the same credentials.
    ///
    /// Word forms only, and each one names a state the *user* has to change
    /// (top up, fix the key, enable the account). Nothing here matches on
    /// digits — see the note on the type. Both the underscore and the spaced
    /// spelling are listed where providers are known to use both.
    private static let terminalMarkers: [String] = [
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
        "account is not active"
    ]

    /// Classifies a provider error message.
    ///
    /// - Parameter message: The `message` payload of a
    ///   `StreamingProviderEvent.error`, exactly as the strategy produced it.
    /// - Returns: `.terminal` when the message names a credit, quota, key,
    ///   billing or permission condition; `.transient` otherwise. Unrecognised
    ///   wording — including an empty message — falls through to `.transient`
    ///   on purpose, so a payload nobody has seen yet keeps today's behaviour
    ///   instead of quietly losing its reconnect.
    static func outcome(forProviderMessage message: String) -> Outcome {
        let normalized = message.lowercased()
        let isTerminal = terminalMarkers.contains { normalized.contains($0) }
        return isTerminal ? .terminal : .transient
    }
}
