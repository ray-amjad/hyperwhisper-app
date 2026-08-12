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
            forProviderMessage: "ElevenLabs authentication failed. Please check your API key in Settings."
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
}
