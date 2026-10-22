//
//  StreamingReconnectBudgetTests.swift
//  hyperwhisperTests
//
//  The live-streaming reconnect budget, pinned (#1655).
//
//  `handleUnexpectedDisconnect` decides after every dropped WebSocket whether
//  to reconnect. A drop after a connection that stayed up for 60 s or more is
//  an isolated blip and resets the budget (#246: a long dictation must not die
//  from 3 blips spread over an hour). Rapid drops add up, and the 4th one gives
//  up with "Connection lost after multiple retries".
//
//  The rule lives in `StreamingTranscriptionClient.reconnectBudget`, a pure
//  function that takes the connection's age as an argument, so these cases
//  need no socket, no clock and no sleep.
//

import Foundation
import Testing
@testable import HyperWhisper

@Suite("Streaming reconnect budget")
struct StreamingReconnectBudgetTests {

    private func budget(_ previousCount: Int, _ connectedFor: TimeInterval) -> (count: Int, exhausted: Bool) {
        StreamingTranscriptionClient.reconnectBudget(previousCount: previousCount, connectedFor: connectedFor)
    }

    @Test("The constants are 60 s and 3 reconnects")
    func constants() {
        #expect(StreamingTranscriptionClient.stableConnectionResetInterval == 60)
        #expect(StreamingTranscriptionClient.maxReconnectsPerCycle == 3)
    }

    @Test("A drop after a connection stable for over 60 s resets the count to 1")
    func stableDropResets() {
        let result = budget(2, 61)
        #expect(result.count == 1)
        #expect(!result.exhausted)
    }

    @Test("A drop after exactly 60 s resets the count to 1")
    func dropAtExactlySixtySecondsResets() {
        let result = budget(2, 60)
        #expect(result.count == 1)
        #expect(!result.exhausted)
    }

    @Test("A drop after 59 s adds to the count")
    func rapidDropAccumulates() {
        let result = budget(2, 59)
        #expect(result.count == 3)
        #expect(!result.exhausted)
    }

    @Test("The 3rd rapid drop reconnects, the 4th gives up")
    func fourthRapidDropIsExhausted() {
        var count = 0
        var exhausted: [Bool] = []
        for _ in 0..<4 {
            let result = budget(count, 1)
            count = result.count
            exhausted.append(result.exhausted)
        }
        #expect(exhausted == [false, false, false, true])
        #expect(count == 4)
    }

    @Test("A stable drop after 3 rapid drops is not exhausted (#246)")
    func stableDropAfterThreeRapidDropsIsNotExhausted() {
        let result = budget(3, 120)
        #expect(result.count == 1)
        #expect(!result.exhausted)
    }

    @Test("The first drop of a fresh session counts 1, even on a young connection")
    func firstDropCountsOne() {
        let result = budget(0, 0)
        #expect(result.count == 1)
        #expect(!result.exhausted)
    }
}
