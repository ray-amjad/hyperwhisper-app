//
//  StreamingProviderErrorPolicyTests.swift
//  hyperwhisperTests
//
//  Pins both directions of the terminal/transient split that stops a terminal
//  provider error from fanning out into an unexpected-disconnect plus a doomed
//  reconnect (HYPERWHISPER-RW / -MG / -MH): terminal wording must suppress the
//  reconnect, and everything else — network blips, rate limits, request ids
//  that merely contain "401" — must keep it.
//
//  Fixtures use generic provider wording only: no real endpoints, keys,
//  organisation ids or user ids.
//

import Foundation
import Testing
@testable import HyperWhisper

struct StreamingProviderErrorPolicyTests {

    // MARK: - Terminal

    @Test func noCreditsRemainingIsTerminal() {
        let outcome = StreamingProviderErrorPolicy.outcome(
            forProviderMessage: "You have no credits remaining. Add credits to continue."
        )

        #expect(outcome == .terminal)
    }

    @Test func insufficientQuotaCodeIsTerminal() {
        // The wording that produced this cluster: the machine-readable
        // `insufficient_quota` code is dropped by the strategy's decoder, so it
        // is only ever seen where the provider also echoes it into the message.
        let outcome = StreamingProviderErrorPolicy.outcome(
            forProviderMessage: "insufficient_quota: the account has run out of credit."
        )

        #expect(outcome == .terminal)
    }

    @Test func exceededCurrentQuotaIsTerminal() {
        let outcome = StreamingProviderErrorPolicy.outcome(
            forProviderMessage: "You exceeded your current quota, please check your plan and billing details."
        )

        #expect(outcome == .terminal)
    }

    @Test func incorrectApiKeyIsTerminal() {
        let outcome = StreamingProviderErrorPolicy.outcome(
            forProviderMessage: "Incorrect API key provided. Check the key configured for this provider."
        )

        #expect(outcome == .terminal)
    }

    @Test func unauthorizedIsTerminalRegardlessOfCase() {
        // Pins the `lowercased()` normalisation: providers capitalise these
        // words inconsistently between the status line and the message body.
        let outcome = StreamingProviderErrorPolicy.outcome(
            forProviderMessage: "Unauthorized: authentication failed for this session."
        )

        #expect(outcome == .terminal)
    }

    @Test func forbiddenIsTerminal() {
        let outcome = StreamingProviderErrorPolicy.outcome(
            forProviderMessage: "Forbidden: this key is not permitted to use realtime transcription."
        )

        #expect(outcome == .terminal)
    }

    @Test func inactiveAccountIsTerminal() {
        let outcome = StreamingProviderErrorPolicy.outcome(
            forProviderMessage: "The account is not active. Reactivate it to keep transcribing."
        )

        #expect(outcome == .terminal)
    }

    // MARK: - Transient

    @Test func networkDropIsTransient() {
        // The case auto-reconnect exists for — misclassifying it would remove a
        // working recovery path, which is the expensive direction of this split.
        let outcome = StreamingProviderErrorPolicy.outcome(
            forProviderMessage: "Connection reset by peer while streaming audio."
        )

        #expect(outcome == .transient)
    }

    @Test func rateLimitedMessageIsTransient() {
        // The deliberate asymmetry with `exceededCurrentQuotaIsTerminal`:
        // providers return quota exhaustion under a `rate_limit_error` type,
        // but classification reads the message text, so a plain rate limit —
        // which clears by itself in seconds — stays retryable. This matches
        // `TranscriptionPipeline+ErrorClassification`, where `rate_limited` is
        // retryable while `quota_exceeded` is not.
        let outcome = StreamingProviderErrorPolicy.outcome(
            forProviderMessage: "Rate limit reached for requests. Please try again in 20s."
        )

        #expect(outcome == .transient)
    }

    @Test func requestIdThatContainsFourZeroOneStaysTransient() {
        // Guards the trap the policy is written around: matching a bare "401"
        // (or "403") substring would brand this transient upstream failure
        // terminal, because provider payloads embed request ids with those
        // digits in them. Only word forms may be matched.
        let outcome = StreamingProviderErrorPolicy.outcome(
            forProviderMessage: "Stream interrupted (request_id: req_4013f2c8). Please retry."
        )

        #expect(outcome == .transient)
    }

    @Test func genericServerFailureIsTransient() {
        let outcome = StreamingProviderErrorPolicy.outcome(
            forProviderMessage: "Internal server error while processing the audio stream."
        )

        #expect(outcome == .transient)
    }

    @Test func strategyFallbackMessageIsTransient() {
        // A provider error frame with no message body falls back to a generic
        // string in the strategy. Nothing about it says the credentials are
        // dead, so it must not suppress the reconnect.
        let outcome = StreamingProviderErrorPolicy.outcome(
            forProviderMessage: "Realtime transcription failed"
        )

        #expect(outcome == .transient)
    }

    @Test func emptyMessageIsTransient() {
        // Unknown wording keeps today's behaviour rather than silently losing
        // its reconnect — the default has to be the conservative direction.
        let outcome = StreamingProviderErrorPolicy.outcome(forProviderMessage: "")

        #expect(outcome == .transient)
    }

    // MARK: - Wording this codebase actually emits
    //
    // Everything above is hypothetical provider wording, which guards the
    // matching rules but not the coupling that matters: the policy only helps if
    // it recognises the exact strings the app's own providers put on the wire.
    // The literals below are copied from the strategies and from the HyperWhisper
    // Cloud error frames they parse, so rewording a message without revisiting
    // this classification fails here instead of in production.

    @Test func hyperWhisperCloudCreditExhaustionIsTerminal() {
        // THE FLAGSHIP CASE, and the one the earlier marker list missed: the
        // default provider's credit-exhaustion frame. Missing it meant the
        // provider most users are on still produced the full
        // HYPERWHISPER-MH → -MG → -RW fan-out from a single exhausted balance.
        let outcome = StreamingProviderErrorPolicy.outcome(
            forProviderMessage: "Credit balance exhausted"
        )

        #expect(outcome == .terminal)
    }

    @Test func elevenLabsAuthErrorWordingIsTerminal() {
        // ElevenLabsStreamingStrategy's `auth_error` message, verbatim. It also
        // arrives before the session-started frame, which is the case that has
        // to fail startup rather than wait out the connection timeout.
        let outcome = StreamingProviderErrorPolicy.outcome(
            forProviderMessage: "ElevenLabs authentication failed. Check that your ElevenLabs API key is correct and still active."
        )

        #expect(outcome == .terminal)
    }

    @Test func elevenLabsQuotaExceededWordingIsTerminal() {
        // ElevenLabsStreamingStrategy's `quota_exceeded` message, verbatim.
        let outcome = StreamingProviderErrorPolicy.outcome(
            forProviderMessage: "ElevenLabs quota exceeded. Please check your account billing."
        )

        #expect(outcome == .terminal)
    }

    @Test func elevenLabsRateLimitWordingIsTransient() {
        // ElevenLabsStreamingStrategy's `rate_limited` message, verbatim — the
        // live half of the rate-limit/quota asymmetry. Too many concurrent
        // sockets from one key clears by itself, so the reconnect must survive.
        let outcome = StreamingProviderErrorPolicy.outcome(
            forProviderMessage: "ElevenLabs rate limit reached. Please try again in a moment."
        )

        #expect(outcome == .transient)
    }

    @Test func openAIStrategyFallbackWordingIsTransient() {
        // OpenAIStreamingStrategy's fallback when the error frame carries no
        // message. It says nothing about the account, so it keeps its reconnect.
        // OpenAI's real quota wording is covered by exceededCurrentQuotaIsTerminal.
        let outcome = StreamingProviderErrorPolicy.outcome(
            forProviderMessage: "OpenAI Realtime transcription failed"
        )

        #expect(outcome == .transient)
    }

    @Test func xaiStrategyFallbackWordingIsTransient() {
        // XAIStreamingStrategy's fallback when the error frame carries no message.
        let outcome = StreamingProviderErrorPolicy.outcome(
            forProviderMessage: "xAI streaming transcription failed"
        )

        #expect(outcome == .transient)
    }

    @Test func geminiStrategyFallbackWordingIsTransient() {
        // GeminiStreamingStrategy's fallback when the error frame carries no
        // message. It names no account state, so it keeps its reconnect.
        let outcome = StreamingProviderErrorPolicy.outcome(
            forProviderMessage: "Gemini streaming transcription failed"
        )

        #expect(outcome == .transient)
    }

    @Test func geminiRejectedSetupIsTerminal() {
        // The backend's wording for a Gemini live socket that closed 1007 on the
        // setup handshake (ws-streaming-gemini-transcribe.ts, parseGeminiLiveClose).
        // The setup frame is byte-identical on every attempt, so a reconnect can
        // only reproduce the rejection — this was a guaranteed retry loop before
        // the marker existed.
        let outcome = StreamingProviderErrorPolicy.outcome(
            forProviderMessage: "Transcription service rejected the session setup"
        )

        #expect(outcome == .terminal)
    }

    @Test func geminiRejectedCredentialsIsTerminal() {
        // Google never returns 401 on the live socket; a bad key arrives as text.
        // Phase 4 chose this wording so it matches the `api key not valid` marker.
        let outcome = StreamingProviderErrorPolicy.outcome(
            forProviderMessage: "Transcription service rejected the credentials: API key not valid"
        )

        #expect(outcome == .terminal)
    }

    @Test func standardFatalCloseCodesAreTerminalAndTransientOnesAreNot() {
        // Parity with .NET's IStreamingProviderStrategy.IsTerminalCloseCode. The
        // exclusions matter more than the inclusions: 1006 is the ordinary
        // dropped connection, and marking it terminal would delete auto-reconnect
        // for every flaky-network user.
        for fatal in [1002, 1003, 1007, 1008, 1009, 1011] {
            #expect(StreamingProviderErrorPolicy.isTerminalCloseCode(fatal))
        }
        for transient in [1000, 1001, 1006, 1012, 1013] {
            #expect(!StreamingProviderErrorPolicy.isTerminalCloseCode(transient))
        }
        // HyperWhisper's own codes stay out: the client handles them by name, and
        // 4001 has to report itself rather than only stop retrying.
        #expect(!StreamingProviderErrorPolicy.isTerminalCloseCode(4001))
        #expect(!StreamingProviderErrorPolicy.isTerminalCloseCode(4002))
    }

    @Test func onlyDefinitiveInternalErrorCloseChangesProviderHealth() {
        #expect(StreamingProviderErrorPolicy.isProviderUnavailableClose(
            code: 1011,
            reason: nil,
            provider: .hyperwhisperCloud
        ))

        for code in [1000, 1002, 1003, 1006, 1007, 1008, 1009, 1012, 4001, 4002] {
            #expect(!StreamingProviderErrorPolicy.isProviderUnavailableClose(
                code: code,
                reason: "NET-0000",
                provider: .deepgram
            ))
        }
    }

    @Test func deepgram1011UsesItsStructuredReason() {
        #expect(StreamingProviderErrorPolicy.isProviderUnavailableClose(
            code: 1011,
            reason: "NET-0000 Internal server error",
            provider: .deepgram
        ))

        for reason in ["NET-0001 timeout", "NET-0002 no_audio_timeout", "no_audio_timeout", nil] {
            #expect(!StreamingProviderErrorPolicy.isProviderUnavailableClose(
                code: 1011,
                reason: reason,
                provider: .deepgram
            ))
        }
    }

    @Test func hyperWhisperCloudStrategyFallbackWordingIsTransient() {
        // HyperWhisperCloudStrategy's fallback for an error frame with no
        // message body.
        let outcome = StreamingProviderErrorPolicy.outcome(
            forProviderMessage: "Unknown server error"
        )

        #expect(outcome == .transient)
    }

    @Test func hyperWhisperCloudTransientFramesKeepTheirReconnect() {
        // The rest of the HyperWhisper Cloud error frames. Every one of these is
        // a service-side or in-flight condition that a fresh socket can recover
        // from, so classifying any of them terminal would delete a working
        // recovery path — the expensive direction of this split.
        //
        // "Deepgram API key not configured" names the *service's* upstream key,
        // not the user's, so it is deliberately not treated as one of the
        // user-fixable account states; it falls through to the conservative
        // default and keeps today's behaviour.
        let messages = [
            "Transcription service error",
            "Transcription service busy, audio dropped",
            "Audio stream too large",
            "Audio chunk too large",
            "WebSocket error",
            "Deepgram API key not configured"
        ]

        for message in messages {
            #expect(
                StreamingProviderErrorPolicy.outcome(forProviderMessage: message) == .transient,
                "expected transient for \(message)"
            )
        }
    }

    // MARK: - Refused upgrades

    @Test func paymentRequiredUpgradeIsInsufficientCredits() {
        // The other half of running out of credits: the user who already has
        // none. HyperWhisper Cloud needs 30 seconds of balance to open a
        // streaming session and refuses the upgrade with 402 before a socket
        // exists, so no error frame is ever sent and the message-based split
        // above never gets a turn.
        #expect(StreamingProviderErrorPolicy.upgradeRefusal(forStatus: 402) == .insufficientCredits)
    }

    @Test func refusedCredentialUpgradesAreUnauthorized() {
        // 401 is what the same route answers for a missing or unknown account
        // key. 403 is grouped with it: both mean the key will not open a session
        // until the user changes something.
        #expect(StreamingProviderErrorPolicy.upgradeRefusal(forStatus: 401) == .unauthorized)
        #expect(StreamingProviderErrorPolicy.upgradeRefusal(forStatus: 403) == .unauthorized)
    }

    @Test func nonUserUpgradeStatusesAreNotAccountRefusals() {
        // These statuses do not name an account action. The client classifies
        // 5xx separately as provider unavailable; the rest keep normal handling.
        let statuses = [0, 200, 400, 404, 408, 429, 500, 502, 503, 504]

        for status in statuses {
            #expect(
                StreamingProviderErrorPolicy.upgradeRefusal(forStatus: status) == nil,
                "expected no refusal for HTTP \(status)"
            )
        }
    }


    @Test func onlyFiveHundredsMakeAnUpgradeProviderUnavailable() {
        for status in [500, 502, 503, 504, 599] {
            #expect(StreamingProviderErrorPolicy.isProviderUnavailableUpgradeStatus(status))
        }
        for status in [0, 101, 400, 401, 402, 403, 408, 429, 600] {
            #expect(!StreamingProviderErrorPolicy.isProviderUnavailableUpgradeStatus(status))
        }
    }

    @Test func aSuccessfulUpgradeIsNotARefusal() {
        // 101 is the status a socket that actually opened carries, so every
        // mid-session drop reads its response and finds nothing here. Without
        // this the refusal check would swallow ordinary disconnects.
        #expect(StreamingProviderErrorPolicy.upgradeRefusal(forStatus: 101) == nil)
    }
}

// MARK: - Health provider identity

struct StreamingHealthProviderIdentityTests {

    @Test func everyRemoteStreamMapsToItsActualCloudHealthProvider() {
        let expected: [StreamingTranscriptionProvider: CloudProvider] = [
            .hyperwhisperCloud: .hyperwhisper,
            .deepgram: .deepgram,
            .elevenLabs: .elevenLabs,
            .openAI: .openai,
            .xai: .grok,
            .gemini: .geminiTranscribe,
        ]

        for (streaming, cloud) in expected {
            #expect(streaming.cloudHealthProvider == cloud)
        }
    }

    @Test func localStreamsHaveNoCloudHealthProvider() {
        #expect(StreamingTranscriptionProvider.parakeetLocal.cloudHealthProvider == nil)
        #expect(StreamingTranscriptionProvider.nemotronLocal.cloudHealthProvider == nil)
    }
}

// MARK: - Reporting policy

/// Pins what the recording flow does with a failure once it has one: which
/// faults still earn a Sentry issue, and which sentence the user reads.
struct StreamingErrorReportingPolicyTests {

    @Test func exhaustedCreditsAreNotReportedTwice() {
        // `StreamingTranscriptionClient` already captured this fault, tagged
        // `terminal`. The flow's own capture titled it "Streaming WebSocket
        // error" — an outage headline on a user who only has to top up.
        #expect(StreamingErrorReportingPolicy.shouldCaptureInSentry(StreamingError.insufficientCredits) == false)
        #expect(StreamingErrorReportingPolicy.shouldCaptureInSentry(StreamingError.unauthorized) == false)
    }

    @Test func aTerminalProviderFrameIsNotReportedTwice() {
        // The mid-session half, which reaches the flow wrapped as a serverError
        // carrying the provider's untranslated wording.
        let error = StreamingError.serverError("Credit balance exhausted")

        #expect(StreamingErrorReportingPolicy.shouldCaptureInSentry(error) == false)
    }

    @Test func genuineFailuresAreStillReported() {
        // The direction that must not regress: every fault the app itself owns
        // keeps its Sentry issue.
        let errors: [StreamingError] = [
            .connectionTimeout,
            .invalidURL,
            .serverError("Transcription service error"),
            .audioEngineError("Failed to install tap")
        ]

        for error in errors {
            #expect(
                StreamingErrorReportingPolicy.shouldCaptureInSentry(error),
                "expected a capture for \(error)"
            )
        }
    }

    @Test func anUnfamiliarErrorKeepsItsReport() {
        // The conservative default. A failure that never passed through the
        // streaming client is unrecognised here, and unrecognised must mean
        // "report it", not "drop it".
        struct SomethingElse: Error {}

        #expect(StreamingErrorReportingPolicy.shouldCaptureInSentry(SomethingElse()))
    }

    @Test func aUserFixableFaultLeadsWithItsOwnMessage() {
        // "Streaming error: Insufficient credits…" leads with the app's failure
        // and buries the fix. The description already names both.
        let message = StreamingErrorReportingPolicy.userMessage(
            for: StreamingError.insufficientCredits,
            context: "Streaming error: "
        )

        #expect(message == StreamingError.insufficientCredits.localizedDescription)
        #expect(!message.hasPrefix("Streaming error: "))
    }

    @Test func anAppFaultKeepsItsFraming() {
        let message = StreamingErrorReportingPolicy.userMessage(
            for: StreamingError.connectionTimeout,
            context: "Streaming error: "
        )

        #expect(message == "Streaming error: Connection timed out")
    }
}
