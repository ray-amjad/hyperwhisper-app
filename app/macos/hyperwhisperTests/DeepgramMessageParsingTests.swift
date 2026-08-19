//
//  DeepgramMessageParsingTests.swift
//  hyperwhisperTests
//
//  Regression coverage for issue #106: Deepgram overloads the "channel" JSON
//  field — an object with `alternatives` on "Results" frames, but a bare
//  channel-index array (e.g. [0,1]) on "SpeechStarted"/"UtteranceEnd" frames.
//  Before the fix, DeepgramStreamingStrategy's private DeepgramMessage relied
//  on the compiler-synthesized Decodable init, which decoded `channel`
//  strictly and threw DecodingError.typeMismatch on the array shape — failing
//  to decode the *entire* message (including `type`) and silently dropping
//  it in parseMessage's outer do/catch. That made
//  `case "UtteranceEnd", "SpeechStarted"` unreachable in practice.
//

import Foundation
import Testing
@testable import HyperWhisper

struct DeepgramMessageParsingTests {

    @Test func resultsWithObjectChannelProducesFinalTranscript() {
        let strategy = DeepgramStreamingStrategy()
        let json = #"{"type":"Results","channel":{"alternatives":[{"transcript":"hello"}]},"is_final":true}"#

        guard let event = strategy.parseMessage(json) else {
            Issue.record("expected an event, got nil")
            return
        }

        guard case .finalTranscript(let text) = event else {
            Issue.record("expected .finalTranscript, got \(event)")
            return
        }
        #expect(text == "hello")
    }

    @Test func speechStartedWithArrayChannelDoesNotFailToDecode() {
        // Issue #106: "channel" is [0,1] here, not an object. Before the fix this
        // threw during decode and parseMessage returned nil for every SpeechStarted
        // frame instead of reaching the "SpeechStarted" case in the switch.
        let strategy = DeepgramStreamingStrategy()
        let json = #"{"type":"SpeechStarted","channel":[0,1],"timestamp":1.2}"#

        guard let event = strategy.parseMessage(json) else {
            Issue.record("expected an event, got nil — array-shaped channel likely failed to decode")
            return
        }

        guard case .metadata(let raw) = event else {
            Issue.record("expected .metadata, got \(event)")
            return
        }
        #expect(raw == json)
    }

    @Test func utteranceEndWithArrayChannelDoesNotFailToDecode() {
        // Same overloaded "channel" shape as SpeechStarted — see issue #106.
        let strategy = DeepgramStreamingStrategy()
        let json = #"{"type":"UtteranceEnd","channel":[0,1],"last_word_end":2.5}"#

        guard let event = strategy.parseMessage(json) else {
            Issue.record("expected an event, got nil — array-shaped channel likely failed to decode")
            return
        }

        guard case .metadata(let raw) = event else {
            Issue.record("expected .metadata, got \(event)")
            return
        }
        #expect(raw == json)
    }
}
