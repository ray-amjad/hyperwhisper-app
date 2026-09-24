//
//  SoundEffectsManagerTests.swift
//  hyperwhisperTests
//

import Foundation
import os
import Testing
@testable import HyperWhisper

/// A fake player whose `play()` blocks until the test releases it, so a test
/// can see where and when the manager loads and plays a sound.
private final class BlockingSoundPlayer: SoundEffectPlayer, @unchecked Sendable {
    private struct State {
        var loadRanOnMainThread: Bool?
        var playRanOnMainThread: Bool?
        var finishedPlay = false
    }

    private let state = OSAllocatedUnfairLock(initialState: State())
    private let entered = DispatchSemaphore(value: 0)
    private let releaseSemaphore = DispatchSemaphore(value: 0)

    var volume: Float = 1
    var currentTime: TimeInterval = 3

    var loadRanOnMainThread: Bool? { state.withLock { $0.loadRanOnMainThread } }
    var playRanOnMainThread: Bool? { state.withLock { $0.playRanOnMainThread } }
    var finishedPlay: Bool { state.withLock { $0.finishedPlay } }

    func recordLoad() {
        state.withLock { $0.loadRanOnMainThread = Thread.isMainThread }
    }

    @discardableResult func play() -> Bool {
        state.withLock { $0.playRanOnMainThread = Thread.isMainThread }
        entered.signal()
        _ = releaseSemaphore.wait(timeout: .now() + 5)
        state.withLock { $0.finishedPlay = true }
        return true
    }

    func waitUntilBlocked() async -> Bool {
        await Task.detached {
            self.entered.wait(timeout: .now() + 5) == .success
        }.value
    }

    func release() {
        releaseSemaphore.signal()
    }
}

/// HYPERWHISPER-KY: the start and stop sounds must never load or play on the
/// main thread, because every AVAudioPlayer call can wait on a busy HAL.
@MainActor
struct SoundEffectsManagerTests {

    private static func makeManager(
        start: BlockingSoundPlayer,
        stop: BlockingSoundPlayer
    ) -> SoundEffectsManager {
        SoundEffectsManager { name in
            let player = name == "start1_quarter" ? start : stop
            player.recordLoad()
            return player
        }
    }

    @Test func startSoundReturnsWhilePlayerIsBlockedOffMain() async {
        let start = BlockingSoundPlayer()
        let stop = BlockingSoundPlayer()
        defer { start.release() }
        let manager = Self.makeManager(start: start, stop: stop)

        manager.playStartSound(volume: 0.5)

        // An inline play would have run to its 5 s release timeout by now.
        #expect(start.finishedPlay == false)
        #expect(await start.waitUntilBlocked())
        #expect(start.loadRanOnMainThread == false)
        #expect(start.playRanOnMainThread == false)
        #expect(start.volume == 0.5)
        #expect(start.currentTime == 0)
        #expect(stop.playRanOnMainThread == nil)
    }

    @Test func stopSoundPlaysStopPlayerOffMain() async {
        let start = BlockingSoundPlayer()
        let stop = BlockingSoundPlayer()
        defer { stop.release() }
        let manager = Self.makeManager(start: start, stop: stop)

        manager.playStopSound(volume: 0.25)

        #expect(stop.finishedPlay == false)
        #expect(await stop.waitUntilBlocked())
        #expect(stop.loadRanOnMainThread == false)
        #expect(stop.playRanOnMainThread == false)
        #expect(stop.volume == 0.25)
        #expect(start.playRanOnMainThread == nil)
    }
}
