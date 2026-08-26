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
///
/// - Note: Since #281 this type is a thin facade. The twenty markers, the
///   matching rule and the status table live in `hw_net::live::policy` and are
///   reached through `liveClassifyErrorMessage` / `liveUpgradeRefusal`, so
///   Windows and Linux — which never had this policy — get the same answers
///   from the same source. The rationale above is duplicated in the Rust doc
///   comments; change both together. `StreamingProviderErrorPolicyTests` is
///   unchanged on purpose: it is now the conformance proof that the port is
///   behaviour-identical.
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

    /// Classifies a provider error message.
    ///
    /// The twenty markers and the lowercased-substring rule live in
    /// `hw_net::live::policy::classify_error_message`.
    ///
    /// - Parameter message: The `message` payload of a
    ///   `StreamingProviderEvent.error`, exactly as the strategy produced it.
    /// - Returns: `.terminal` when the message names a credit, quota, key,
    ///   billing or permission condition; `.transient` otherwise. Unrecognised
    ///   wording — including an empty message — falls through to `.transient`
    ///   on purpose, so a payload nobody has seen yet keeps today's behaviour
    ///   instead of quietly losing its reconnect.
    static func outcome(forProviderMessage message: String) -> Outcome {
        switch liveClassifyErrorMessage(message: message) {
        case .terminal:
            return .terminal
        case .transient:
            return .transient
        }
    }

    // MARK: - Refused upgrades

    /// Why a server refused the WebSocket upgrade outright, when the refusal is
    /// one the user has to act on.
    ///
    /// Distinct from the message-based classification above because it arrives
    /// through a different door and carries a different signal. A frame is only
    /// ever sent over a socket that opened; these statuses mean the socket never
    /// opened at all, so no strategy ever gets to parse a message and the HTTP
    /// status is the whole of what the server said.
    enum UpgradeRefusal: Equatable {
        /// HTTP 402. The account has no balance to open a session with.
        case insufficientCredits
        /// HTTP 401 / 403. The key is missing, wrong, revoked or not permitted.
        case unauthorized
    }

    /// Classifies the HTTP status of a WebSocket upgrade that never reached 101.
    ///
    /// ## Why this exists
    ///
    /// The message-based classification above only covers a user who runs out of
    /// credits *during* a session. The same user one keypress later hits a
    /// different path entirely: HyperWhisper Cloud requires 30 seconds of
    /// balance to open a streaming session at all
    /// (`ws-streaming-deepgram.ts` — `validateCredits(…, minimumStreamingCredits())`),
    /// and refuses the upgrade with 402 before any socket exists. `receive()`
    /// then fails with a plain transport error carrying none of that: the client
    /// read it as an unexpected disconnect, reported it, retried three times
    /// into the same 402, reported the exhausted retries, and told the user
    /// "Connection lost after multiple retries" — five Sentry events and a
    /// message naming the wrong problem, repeated on every attempt until the
    /// user tops up. The status is on `URLSessionWebSocketTask.response`; it was
    /// simply never read.
    ///
    /// ## Why only these three statuses
    ///
    /// Each names a state the *user* changes (top up, fix the key), which is the
    /// same bar `terminalMarkers` holds to. Everything else — 429, 5xx, a proxy
    /// mangling the upgrade — keeps today's reconnect, because a retry a second
    /// later is a reasonable answer to it. `nil` for an unrecognised status is
    /// therefore the safe default, not a gap.
    ///
    /// - Parameter status: The HTTP status of the response that came back
    ///   instead of a `101 Switching Protocols`. Stays `Int` because that is
    ///   what `URLSessionWebSocketTask.response` hands over; the core takes a
    ///   `UInt16`, so anything outside its range cannot be an HTTP status and
    ///   takes the same "no refusal" answer every other unrecognised status
    ///   gets.
    /// - Returns: The refusal when the user has to act, `nil` when the ordinary
    ///   reconnect path still applies.
    static func upgradeRefusal(forStatus status: Int) -> UpgradeRefusal? {
        guard let code = UInt16(exactly: status),
              let refusal = liveUpgradeRefusal(status: code) else { return nil }
        switch refusal {
        case .insufficientCredits:
            return .insufficientCredits
        case .unauthorized:
            return .unauthorized
        }
    }
}
